namespace VrcVa.Core;

public interface ICaptureSource
{
    Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken);
}

public interface IOcrEngine
{
    Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}

public interface IOcrRegionSource
{
    Task<IReadOnlyList<CapturedFrame>> CreateRegionsAsync(
        CapturedFrame frame,
        CancellationToken cancellationToken);
}

public interface ITextTranslator
{
    Task<TranslationOutput> TranslateToJapaneseAsync(
        string sourceText,
        CancellationToken cancellationToken);
}

public sealed record TextModelRequest(
    string Model,
    string Instructions,
    string Input,
    int MaxOutputTokens);

public sealed record TextModelResponse(
    string Text,
    string Provider,
    string Model);

public interface ITextModelClient
{
    Task<TextModelResponse> GenerateAsync(
        TextModelRequest request,
        CancellationToken cancellationToken);
}

public interface IAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(
        CapturedFrame frame,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Receives a completed in-memory transcript without acquiring a frame or running OCR.
/// The handler owns its feature-specific query and result, never the shared transcript.
/// </summary>
public interface ITextFeatureHandler
{
    Task<FeatureResult> HandleAsync(
        TextInputSession input,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IResultRenderer
{
    Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken);

    Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken);
}

public interface IPrivacySafeLogger
{
    void Info(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        TimeSpan? duration = null,
        IReadOnlyDictionary<string, long>? numericMetrics = null);

    void Error(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        ScanFailureCode failureCode,
        Exception exception);
}
