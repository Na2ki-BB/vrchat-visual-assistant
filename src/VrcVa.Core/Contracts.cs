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

public interface IAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(
        CapturedFrame frame,
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
