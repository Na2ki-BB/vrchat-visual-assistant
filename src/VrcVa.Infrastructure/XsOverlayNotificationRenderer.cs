using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

public interface IXsOverlayNotificationSink
{
    Task SendAsync(
        string title,
        string content,
        XsOverlayNotificationKind kind,
        CancellationToken cancellationToken);
}

public enum XsOverlayNotificationKind
{
    Progress,
    Result,
    Error,
}

public sealed class XsOverlayUdpNotificationSink(int port = 42069)
    : IXsOverlayNotificationSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly int _port = port is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port));

    public async Task SendAsync(
        string title,
        string content,
        XsOverlayNotificationKind kind,
        CancellationToken cancellationToken)
    {
        (int timeout, int height, double volume, string audioPath) = kind switch
        {
            XsOverlayNotificationKind.Progress => (1, 90, 0.08, "default"),
            XsOverlayNotificationKind.Result => (12, 180, 0.15, "default"),
            XsOverlayNotificationKind.Error => (15, 180, 0.35, "error"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        XsOverlayNotification message = new(
            MessageType: 1,
            Title: title,
            Content: content,
            Timeout: timeout,
            Height: height,
            Opacity: 1,
            Volume: volume,
            AudioPath: audioPath,
            UseBase64Icon: false,
            Icon: "default",
            SourceApp: "VRChat Visual Assistant");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);

        using UdpClient client = new(AddressFamily.InterNetwork);
        await client.SendAsync(
            payload,
            new IPEndPoint(IPAddress.Loopback, _port),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record XsOverlayNotification(
        int MessageType,
        string Title,
        string Content,
        int Timeout,
        int Height,
        double Opacity,
        double Volume,
        string AudioPath,
        bool UseBase64Icon,
        string Icon,
        string SourceApp);
}

public sealed class XsOverlayNotificationRenderer(IXsOverlayNotificationSink sink)
    : IResultRenderer
{
    private const int MaxNotificationCharacters = 700;

    public Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken) =>
        progress.Stage == ScanStage.Trigger
            ? sink.SendAsync(
                "SCAN中…",
                "画面を取得しています。",
                XsOverlayNotificationKind.Progress,
                cancellationToken)
            : Task.CompletedTask;

    public Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.IsSuccess && outcome.Result is not null)
        {
            return Task.CompletedTask;
        }

        if (!outcome.IsSuccess || outcome.Result is null)
        {
            ScanFailure? failure = outcome.Failure;
            string stage = failure?.Stage.ToString() ?? "Unknown";
            return sink.SendAsync(
                $"SCAN失敗 — {stage}",
                Clip(failure?.Message ?? "不明なエラーが発生しました。"),
                XsOverlayNotificationKind.Error,
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    private static string Clip(string value) =>
        value.Length <= MaxNotificationCharacters
            ? value
            : string.Concat(value.AsSpan(0, MaxNotificationCharacters - 1), "…");
}
