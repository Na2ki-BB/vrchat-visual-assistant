using System.Security.Cryptography;

namespace VrcVa.Core;

public enum ScanStage
{
    Trigger,
    Capture,
    Ocr,
    Translation,
    Rendering,
    Completed,
}

public enum ScanFailureCode
{
    Busy,
    CaptureTargetNotFound,
    CaptureUnavailable,
    OcrUnavailable,
    NoTextDetected,
    TranslationNotConfigured,
    TranslationAuthenticationFailed,
    TranslationInputTooLarge,
    TranslationUsageLimitReached,
    TranslationRateLimited,
    TranslationTimedOut,
    TranslationFailed,
    Cancelled,
    Unexpected,
}

public sealed record ScanRequest(
    Guid CorrelationId,
    DateTimeOffset RequestedAt,
    string TriggerName)
{
    public static ScanRequest Create(string triggerName) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, triggerName);
}

public sealed class CapturedFrame : IDisposable
{
    private byte[]? _encodedImage;

    public CapturedFrame(
        byte[] encodedImage,
        int width,
        int height,
        string mediaType,
        string sourceKind,
        int? ocrScaleReferenceWidth = null,
        int? ocrScaleReferenceHeight = null)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);

        if (encodedImage.Length == 0)
        {
            throw new ArgumentException("The captured frame cannot be empty.", nameof(encodedImage));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        if (ocrScaleReferenceWidth is <= 0 || ocrScaleReferenceHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ocrScaleReferenceWidth),
                "OCR scale-reference dimensions must be positive.");
        }

        if (ocrScaleReferenceWidth.HasValue != ocrScaleReferenceHeight.HasValue)
        {
            throw new ArgumentException(
                "Both OCR scale-reference dimensions must be supplied together.");
        }

        _encodedImage = encodedImage;
        Width = width;
        Height = height;
        MediaType = mediaType;
        SourceKind = sourceKind;
        OcrScaleReferenceWidth = ocrScaleReferenceWidth ?? width;
        OcrScaleReferenceHeight = ocrScaleReferenceHeight ?? height;
    }

    public int Width { get; }

    public int Height { get; }

    public string MediaType { get; }

    public string SourceKind { get; }

    public int OcrScaleReferenceWidth { get; }

    public int OcrScaleReferenceHeight { get; }

    public ReadOnlyMemory<byte> EncodedImage =>
        _encodedImage ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public void Dispose()
    {
        byte[]? encodedImage = Interlocked.Exchange(ref _encodedImage, null);
        if (encodedImage is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(encodedImage);
    }
}

public sealed record OcrOutput(
    string Text,
    string RecognizerLanguage,
    string? Warning = null);

public sealed record TranslationOutput(
    string Text,
    string Provider,
    string Model);

public sealed record AnalysisResult(
    string SourceText,
    string JapaneseText,
    string OcrLanguage,
    string TranslationProvider,
    string TranslationModel,
    TimeSpan OcrDuration,
    TimeSpan TranslationDuration,
    string? Warning = null,
    string CaptureSourceKind = "unknown");

public sealed record ScanProgress(
    Guid CorrelationId,
    ScanStage Stage,
    string Message,
    TimeSpan Elapsed);

public sealed record ScanFailure(
    ScanFailureCode Code,
    ScanStage Stage,
    string Message);

public sealed record ScanOutcome(
    Guid CorrelationId,
    bool IsSuccess,
    AnalysisResult? Result,
    ScanFailure? Failure,
    TimeSpan TotalDuration)
{
    public static ScanOutcome Succeeded(
        Guid correlationId,
        AnalysisResult result,
        TimeSpan totalDuration) =>
        new(correlationId, true, result, null, totalDuration);

    public static ScanOutcome Failed(
        Guid correlationId,
        ScanFailure failure,
        TimeSpan totalDuration) =>
        new(correlationId, false, null, failure, totalDuration);
}
