using System.Net;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.ServiceDiscovery.Dnssd;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace VrcVa.Windows.Osc;

internal sealed class OscTriggerService : IDisposable
{
    private const string ServiceName = "VRChat Visual Assistant";
    private readonly object _gateSync = new();
    private readonly OscTriggerOptions _options;
    private readonly OscTriggerGate _triggerGate;
    private readonly DatagramSocket _oscSocket;
    private readonly StreamSocketListener _querySocket;
    private bool _disposed;
    private int _activeQueryConnections;
    private long _receivedDatagrams;
    private long _acceptedLocalDatagrams;
    private long _parsedMessages;
    private long _matchingMessages;
    private long _queryConnections;
    private long _acceptedLocalQueryConnections;

    private OscTriggerService(
        OscTriggerOptions options,
        DatagramSocket oscSocket,
        StreamSocketListener querySocket)
    {
        _options = options;
        _triggerGate = new OscTriggerGate(options.Debounce);
        _oscSocket = oscSocket;
        _querySocket = querySocket;
        _oscSocket.MessageReceived += OscSocket_MessageReceived;
        _querySocket.ConnectionReceived += QuerySocket_ConnectionReceived;
    }

    internal event EventHandler? Triggered;

    internal event EventHandler<Exception>? Faulted;

    internal ushort OscPort { get; private set; }

    internal ushort QueryPort { get; private set; }

    internal long ReceivedDatagrams => Interlocked.Read(ref _receivedDatagrams);

    internal long AcceptedLocalDatagrams => Interlocked.Read(ref _acceptedLocalDatagrams);

    internal long ParsedMessages => Interlocked.Read(ref _parsedMessages);

    internal long MatchingMessages => Interlocked.Read(ref _matchingMessages);

    internal long QueryConnections => Interlocked.Read(ref _queryConnections);

    internal long AcceptedLocalQueryConnections =>
        Interlocked.Read(ref _acceptedLocalQueryConnections);

    internal static async Task<OscTriggerService> StartAsync(
        OscTriggerOptions options,
        CancellationToken cancellationToken = default,
        bool strictLoopbackBinding = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            throw new InvalidOperationException("OSC trigger is disabled.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        DatagramSocket oscSocket = new();
        StreamSocketListener querySocket = new();
        OscTriggerService service = new(options, oscSocket, querySocket);
        try
        {
            HostName? bindHost = strictLoopbackBinding
                ? new HostName(IPAddress.Loopback.ToString())
                : null;
            await oscSocket.BindEndpointAsync(bindHost, string.Empty)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            await querySocket.BindEndpointAsync(bindHost, string.Empty)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            service.OscPort = ParsePort(oscSocket.Information.LocalPort);
            service.QueryPort = ParsePort(querySocket.Information.LocalPort);

            HostName registrationHost = strictLoopbackBinding
                ? new HostName("localhost")
                : NetworkInformation.GetHostNames().First(hostName =>
                    hostName.Type == HostNameType.DomainName
                    && hostName.RawName.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
            DnssdServiceInstance oscAdvertisement = new(
                $"{ServiceName}._osc._udp.local.",
                registrationHost,
                service.OscPort);
            DnssdRegistrationResult oscRegistration;
            try
            {
                oscRegistration = await oscAdvertisement
                    .RegisterDatagramSocketAsync(oscSocket)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    "Windows rejected the OSC DNS-SD registration.",
                    exception);
            }
            EnsureRegistrationSucceeded(oscRegistration, "OSC");

            DnssdServiceInstance queryAdvertisement = new(
                $"{ServiceName}._oscjson._tcp.local.",
                registrationHost,
                service.QueryPort);
            DnssdRegistrationResult queryRegistration;
            try
            {
                queryRegistration = await queryAdvertisement
                    .RegisterStreamSocketListenerAsync(querySocket)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    "Windows rejected the OSCQuery DNS-SD registration.",
                    exception);
            }
            EnsureRegistrationSucceeded(queryRegistration, "OSCQuery");

            return service;
        }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _oscSocket.MessageReceived -= OscSocket_MessageReceived;
        _querySocket.ConnectionReceived -= QuerySocket_ConnectionReceived;
        _oscSocket.Dispose();
        _querySocket.Dispose();
    }

    internal static OscQueryResponse CreateQueryResponse(
        string requestTarget,
        string triggerAddress,
        ushort oscPort,
        OscTriggerValueType valueType = OscTriggerValueType.Boolean)
    {
        int queryIndex = requestTarget.IndexOf('?', StringComparison.Ordinal);
        string query = queryIndex < 0 ? string.Empty : requestTarget[(queryIndex + 1)..];
        if (string.Equals(query, "HOST_INFO", StringComparison.OrdinalIgnoreCase))
        {
            return OscQueryResponse.Ok(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["NAME"] = ServiceName,
                ["OSC_IP"] = IPAddress.Loopback.ToString(),
                ["OSC_PORT"] = oscPort,
                ["OSC_TRANSPORT"] = "UDP",
                ["EXTENSIONS"] = new Dictionary<string, bool>(),
            }));
        }

        if (query.Length > 0)
        {
            return OscQueryResponse.NoContent();
        }

        string path = GetRequestPath(requestTarget);
        string parameterName = triggerAddress["/avatar/parameters/".Length..];
        Dictionary<string, object?> triggerNode = new()
        {
            ["FULL_PATH"] = triggerAddress,
            ["TYPE"] = valueType == OscTriggerValueType.Boolean ? "T" : "i",
        };
        Dictionary<string, object?> parametersNode = new()
        {
            ["FULL_PATH"] = "/avatar/parameters",
            ["CONTENTS"] = new Dictionary<string, object?>
            {
                [parameterName] = triggerNode,
            },
        };
        Dictionary<string, object?> avatarNode = new()
        {
            ["FULL_PATH"] = "/avatar",
            ["CONTENTS"] = new Dictionary<string, object?>
            {
                ["change"] = new Dictionary<string, object?>
                {
                    ["FULL_PATH"] = "/avatar/change",
                    ["TYPE"] = "s",
                },
                ["parameters"] = parametersNode,
            },
        };

        object? node = path switch
        {
            "/" => new Dictionary<string, object?>
            {
                ["FULL_PATH"] = "/",
                ["CONTENTS"] = new Dictionary<string, object?>
                {
                    ["avatar"] = avatarNode,
                },
            },
            "/avatar" => avatarNode,
            "/avatar/parameters" => parametersNode,
            _ when string.Equals(path, triggerAddress, StringComparison.Ordinal) => triggerNode,
            _ => null,
        };

        return node is null
            ? OscQueryResponse.NotFound()
            : OscQueryResponse.Ok(JsonSerializer.Serialize(node));
    }

    private static string GetRequestPath(string requestTarget)
    {
        int queryIndex = requestTarget.IndexOf('?', StringComparison.Ordinal);
        string path = queryIndex < 0 ? requestTarget : requestTarget[..queryIndex];
        return Uri.TryCreate(path, UriKind.Absolute, out Uri? absoluteUri)
            ? absoluteUri.AbsolutePath
            : path;
    }

    private static ushort ParsePort(string value) =>
        ushort.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out ushort port)
            && port != 0
                ? port
                : throw new InvalidOperationException("Windows did not assign a usable OSC port.");

    private static HashSet<IPAddress> GetLocalHostAddresses()
    {
        HashSet<IPAddress> addresses =
        [
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
        ];
        foreach (HostName hostName in NetworkInformation.GetHostNames())
        {
            if (IPAddress.TryParse(hostName.RawName, out IPAddress? address))
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private bool IsLocalHost(HostName? remoteHost)
    {
        return IsLocalHostAddress(remoteHost?.RawName, GetLocalHostAddresses());
    }

    internal static bool IsLocalHostAddress(
        string? rawAddress,
        IReadOnlySet<IPAddress> localHostAddresses)
    {
        ArgumentNullException.ThrowIfNull(localHostAddresses);
        if (rawAddress is null
            || !IPAddress.TryParse(rawAddress, out IPAddress? remoteAddress))
        {
            return false;
        }

        return IPAddress.IsLoopback(remoteAddress)
            || localHostAddresses.Contains(remoteAddress);
    }

    private static void EnsureRegistrationSucceeded(
        DnssdRegistrationResult result,
        string serviceKind)
    {
        if (result.Status != DnssdRegistrationStatus.Success)
        {
            throw new InvalidOperationException(
                $"{serviceKind} DNS-SD registration failed with status {result.Status}.");
        }
    }

    private void OscSocket_MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
    {
        try
        {
            Interlocked.Increment(ref _receivedDatagrams);
            if (!IsLocalHost(args.RemoteAddress))
            {
                return;
            }

            Interlocked.Increment(ref _acceptedLocalDatagrams);

            using DataReader reader = args.GetDataReader();
            uint length = reader.UnconsumedBufferLength;
            if (length is 0 or > 1024)
            {
                return;
            }

            byte[] packet = new byte[length];
            reader.ReadBytes(packet);
            if (!OscPacketParser.TryParse(packet, out IReadOnlyList<OscMessage> messages))
            {
                return;
            }

            foreach (OscMessage message in messages)
            {
                Interlocked.Increment(ref _parsedMessages);
                if (string.Equals(message.Address, "/avatar/change", StringComparison.Ordinal)
                    && message.Kind == OscValueKind.String)
                {
                    lock (_gateSync)
                    {
                        _triggerGate.ResetForAvatarChange(
                            Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()));
                    }

                    continue;
                }

                if (!string.Equals(message.Address, _options.Address, StringComparison.Ordinal))
                {
                    continue;
                }

                bool active = _options.ValueType switch
                {
                    OscTriggerValueType.Boolean when message.Kind == OscValueKind.Boolean =>
                        message.BooleanValue,
                    OscTriggerValueType.Integer when message.Kind == OscValueKind.Integer =>
                        message.IntegerValue == _options.ExpectedIntegerValue,
                    _ => false,
                };
                bool expectedType = _options.ValueType switch
                {
                    OscTriggerValueType.Boolean => message.Kind == OscValueKind.Boolean,
                    OscTriggerValueType.Integer => message.Kind == OscValueKind.Integer,
                    _ => false,
                };
                if (!expectedType)
                {
                    continue;
                }

                Interlocked.Increment(ref _matchingMessages);

                bool shouldTrigger;
                lock (_gateSync)
                {
                    shouldTrigger = _triggerGate.Observe(
                        active,
                        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()));
                }

                if (shouldTrigger)
                {
                    Triggered?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Faulted?.Invoke(this, exception);
        }
    }

    private async void QuerySocket_ConnectionReceived(
        StreamSocketListener sender,
        StreamSocketListenerConnectionReceivedEventArgs args)
    {
        using StreamSocket socket = args.Socket;
        Interlocked.Increment(ref _queryConnections);
        if (!IsLocalHost(socket.Information.RemoteAddress))
        {
            return;
        }

        Interlocked.Increment(ref _acceptedLocalQueryConnections);

        if (Interlocked.Increment(ref _activeQueryConnections) > 4)
        {
            Interlocked.Decrement(ref _activeQueryConnections);
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            using DataReader reader = new(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial,
            };
            using MemoryStream requestBuffer = new(capacity: 8192);
            bool headerComplete = false;
            while (requestBuffer.Length < 8192)
            {
                uint remaining = checked((uint)(8192 - requestBuffer.Length));
                uint loaded = await reader.LoadAsync(remaining).AsTask(timeout.Token);
                if (loaded == 0)
                {
                    break;
                }

                byte[] chunk = new byte[loaded];
                reader.ReadBytes(chunk);
                requestBuffer.Write(chunk);
                if (requestBuffer.GetBuffer().AsSpan(0, checked((int)requestBuffer.Length))
                    .IndexOf("\r\n\r\n"u8) >= 0)
                {
                    headerComplete = true;
                    break;
                }
            }

            if (!headerComplete)
            {
                return;
            }

            string request = Encoding.ASCII.GetString(
                requestBuffer.GetBuffer(),
                0,
                checked((int)requestBuffer.Length));
            string? requestLine = request.Split("\r\n", 2, StringSplitOptions.None).FirstOrDefault();
            string[] parts = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            OscQueryResponse response = parts.Length >= 2
                && string.Equals(parts[0], "GET", StringComparison.Ordinal)
                    ? CreateQueryResponse(parts[1], _options.Address, OscPort, _options.ValueType)
                    : OscQueryResponse.BadRequest();

            byte[] body = Encoding.UTF8.GetBytes(response.Body);
            string headers =
                $"HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n";
            using DataWriter writer = new(socket.OutputStream);
            writer.WriteBytes(Encoding.ASCII.GetBytes(headers));
            writer.WriteBytes(body);
            await writer.StoreAsync().AsTask(timeout.Token);
            await writer.FlushAsync().AsTask(timeout.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or System.Runtime.InteropServices.COMException
                or IOException
                or ObjectDisposedException)
        {
            // A local client may disconnect or send an incomplete request. This does not
            // affect the OSC listener and is intentionally not written to the persistent log.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Faulted?.Invoke(this, exception);
        }
        finally
        {
            Interlocked.Decrement(ref _activeQueryConnections);
        }
    }
}

internal sealed record OscQueryResponse(int StatusCode, string ReasonPhrase, string Body)
{
    internal static OscQueryResponse Ok(string body) => new(200, "OK", body);

    internal static OscQueryResponse BadRequest() => new(400, "Bad Request", "{}");

    internal static OscQueryResponse NoContent() => new(204, "No Content", string.Empty);

    internal static OscQueryResponse NotFound() => new(404, "Not Found", "{}");
}
