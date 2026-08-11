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
        bool isError,
        CancellationToken cancellationToken);
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
        bool isError,
        CancellationToken cancellationToken)
    {
        XsOverlayNotification message = new(
            MessageType: 1,
            Title: title,
            Content: content,
            Timeout: isError ? 15 : 12,
            Height: 180,
            Opacity: 1,
            Volume: isError ? 0.35 : 0.15,
            AudioPath: isError ? "error" : "default",
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
                "SCAN開始",
                "視界を1回だけ取得して解析します。",
                isError: false,
                cancellationToken)
            : Task.CompletedTask;

    public Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!outcome.IsSuccess || outcome.Result is null)
        {
            ScanFailure? failure = outcome.Failure;
            string stage = failure?.Stage.ToString() ?? "Unknown";
            return sink.SendAsync(
                $"SCAN失敗 — {stage}",
                Clip(failure?.Message ?? "不明なエラーが発生しました。"),
                isError: true,
                cancellationToken);
        }

        bool ocrOnly = string.IsNullOrWhiteSpace(outcome.Result.JapaneseText);
        string title = ocrOnly ? "OCR結果（翻訳未設定）" : "日本語訳";
        string content = ocrOnly
            ? outcome.Result.SourceText
            : outcome.Result.JapaneseText;
        return sink.SendAsync(
            title,
            Clip(content),
            isError: false,
            cancellationToken);
    }

    private static string Clip(string value) =>
        value.Length <= MaxNotificationCharacters
            ? value
            : string.Concat(value.AsSpan(0, MaxNotificationCharacters - 1), "…");
}
