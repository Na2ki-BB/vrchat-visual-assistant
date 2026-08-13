using VrcVa.Windows.Osc;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace VrcVa.Windows.Diagnostics;

internal static class OscTriggerDiagnosticRunner
{
    internal static async Task<int> RunAsync(string[] args)
    {
        int seconds = ParseDurationSeconds(args);
        OscTriggerOptions configured = OscTriggerOptions.FromEnvironment();
        OscTriggerOptions options = configured with { Enabled = true };
        bool strictLoopbackSpike = args.Contains(
            "--strict-loopback-spike",
            StringComparer.OrdinalIgnoreCase);
        using OscTriggerService service = await OscTriggerService.StartAsync(
            options,
            strictLoopbackBinding: strictLoopbackSpike);
        int triggerCount = 0;
        service.Triggered += (_, _) =>
        {
            Interlocked.Increment(ref triggerCount);
            Console.WriteLine("OSC trigger received.");
        };
        service.Faulted += (_, exception) =>
            Console.WriteLine($"OSC diagnostic fault: {exception.GetType().Name}");

        Console.WriteLine($"OSC target advertised to VRChat: 127.0.0.1:{service.OscPort}");
        Console.WriteLine($"OSCQuery service port: {service.QueryPort} (non-local peers are rejected)");
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            using HttpClient queryClient = new()
            {
                Timeout = TimeSpan.FromSeconds(2),
            };
            string hostInfo = await queryClient.GetStringAsync(
                $"http://127.0.0.1:{service.QueryPort}/?HOST_INFO");
            bool querySucceeded = hostInfo.Contains(
                $"\"OSC_PORT\":{service.OscPort}",
                StringComparison.Ordinal);
            Console.WriteLine($"OSCQuery self-test: {(querySucceeded ? "OK" : "FAILED")}");

            using UdpClient sender = new(AddressFamily.InterNetwork);
            IPEndPoint target = new(IPAddress.Loopback, service.OscPort);
            await sender.SendAsync(CreatePacket(options, active: false), target);
            await sender.SendAsync(CreatePacket(options, active: true), target);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            int selfTestCount = Volatile.Read(ref triggerCount);
            Console.WriteLine($"OSC self-test trigger count: {selfTestCount}");
            return querySucceeded && selfTestCount == 1 ? 0 : 10;
        }

        Console.WriteLine($"Waiting {seconds} seconds. No capture, OCR, or translation will run.");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"OSC trigger count: {Volatile.Read(ref triggerCount)}");
        Console.WriteLine(
            "OSC datagrams: "
            + $"received={service.ReceivedDatagrams}, "
            + $"local={service.AcceptedLocalDatagrams}, "
            + $"parsed={service.ParsedMessages}, "
            + $"matching={service.MatchingMessages}");
        Console.WriteLine(
            "OSCQuery connections: "
            + $"received={service.QueryConnections}, "
            + $"local={service.AcceptedLocalQueryConnections}");
        return 0;
    }

    private static int ParseDurationSeconds(string[] args)
    {
        int optionIndex = Array.FindIndex(
            args,
            argument => argument.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
        if (optionIndex < 0)
        {
            return 30;
        }

        if (optionIndex + 1 >= args.Length
            || !int.TryParse(
                args[optionIndex + 1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int seconds)
            || seconds is < 1 or > 300)
        {
            throw new InvalidOperationException("--seconds must be an integer from 1 to 300.");
        }

        return seconds;
    }

    private static byte[] CreatePacket(OscTriggerOptions options, bool active)
    {
        if (options.ValueType == OscTriggerValueType.Boolean)
        {
            return
            [
                .. WritePaddedString(options.Address),
                .. WritePaddedString(active ? ",T" : ",F"),
            ];
        }

        byte[] value = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(
            value,
            active ? options.ExpectedIntegerValue : unchecked(options.ExpectedIntegerValue + 1));
        return
        [
            .. WritePaddedString(options.Address),
            .. WritePaddedString(",i"),
            .. value,
        ];
    }

    private static byte[] WritePaddedString(string value)
    {
        byte[] content = Encoding.UTF8.GetBytes(value);
        int length = (content.Length + 1 + 3) & ~3;
        byte[] bytes = new byte[length];
        content.CopyTo(bytes, 0);
        return bytes;
    }
}
