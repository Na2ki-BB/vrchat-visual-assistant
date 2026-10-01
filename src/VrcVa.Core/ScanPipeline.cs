using System.Diagnostics;

namespace VrcVa.Core;

public sealed class ScanPipeline
{
    private readonly ICaptureSource _captureSource;
    private readonly FeatureCatalog _featureCatalog;
    private readonly IResultRenderer _renderer;
    private readonly IPrivacySafeLogger _logger;
    private readonly ExecutionCoordinator _execution;

    public ScanPipeline(
        ICaptureSource captureSource,
        IAnalyzer analyzer,
        IResultRenderer renderer,
        IPrivacySafeLogger? logger = null,
        ExecutionCoordinator? execution = null)
        : this(
            captureSource,
            new FeatureCatalog(new FeatureEntry(BuiltInFeatures.Translation, analyzer)),
            renderer,
            logger,
            execution)
    {
    }

    public ScanPipeline(
        ICaptureSource captureSource,
        FeatureCatalog featureCatalog,
        IResultRenderer renderer,
        IPrivacySafeLogger? logger = null,
        ExecutionCoordinator? execution = null)
    {
        ArgumentNullException.ThrowIfNull(captureSource);
        ArgumentNullException.ThrowIfNull(featureCatalog);
        ArgumentNullException.ThrowIfNull(renderer);

        _captureSource = captureSource;
        _featureCatalog = featureCatalog;
        _renderer = renderer;
        _logger = logger ?? NullPrivacySafeLogger.Instance;
        _execution = execution ?? new ExecutionCoordinator();
    }

    public async Task<ScanOutcome> RunAsync(
        ScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_execution.TryBeginSession(
            request.CorrelationId,
            out ExecutionOperation? operation,
            cancellationToken,
            request.TextInput?.SessionId))
        {
            return ScanOutcome.Failed(
                request.CorrelationId,
                new ScanFailure(
                    cancellationToken.IsCancellationRequested ? ScanFailureCode.Cancelled : ScanFailureCode.Busy,
                    ScanStage.Trigger,
                    "前の処理を実行中です。完了してからもう一度お試しください。"),
                TimeSpan.Zero);
        }

        using (operation)
        {
            return await RunAsync(request, operation).ConfigureAwait(false);
        }
    }

    /// <summary>Uses admission already owned by the caller, including pre-capture waits.</summary>
    public async Task<ScanOutcome> RunAsync(ScanRequest request, ExecutionOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        if (!_execution.IsActive(operation) || operation.OperationId != request.CorrelationId
            || (request.TextInput is not null && operation.SessionId != request.TextInput.SessionId))
        {
            throw new ArgumentException("The request must match the active operation of this pipeline.", nameof(operation));
        }

        if (!operation.TryClaimExecution())
        {
            return ScanOutcome.Failed(request.CorrelationId,
                new ScanFailure(ScanFailureCode.Busy, ScanStage.Trigger,
                    "この操作は既に実行中、または完了しています。"), TimeSpan.Zero);
        }

        CancellationToken cancellationToken = operation.CancellationToken;
        Stopwatch total = Stopwatch.StartNew();
        ScanStage stage = ScanStage.Trigger;

        try
        {
            operation.ThrowIfNotCurrent();
            FeatureEntry feature = _featureCatalog.Resolve(request.FeatureId);
            if (feature.Descriptor.InputKind != request.InputKind)
            {
                throw new ScanException(
                    ScanFailureCode.InputKindMismatch,
                    ScanStage.Trigger,
                    "この機能では指定された入力を使えません。入力に対応する機能を選び直してください。");
            }

            await RenderProgressAsync(
                request,
                ScanStage.Trigger,
                request.InputKind == FeatureInputKind.CapturedFrame
                    ? "SCANを開始しました。"
                    : "テキスト処理を開始しました。",
                total.Elapsed,
                cancellationToken).ConfigureAwait(false);

            InlineProgress<ScanProgress> progress = new(value =>
            {
                if (_execution.IsActive(operation) && operation.IsCurrent && value.CorrelationId == operation.OperationId)
                {
                    _renderer.RenderProgressAsync(value, cancellationToken).GetAwaiter().GetResult();
                }
            });

            AnalysisResult result;
            if (request.InputKind == FeatureInputKind.Text)
            {
                stage = ScanStage.TextHandling;
                await RenderProgressAsync(
                    request,
                    stage,
                    "認識したテキストを処理しています…",
                    total.Elapsed,
                    cancellationToken).ConfigureAwait(false);

                operation.ThrowIfNotCurrent();
                FeatureResult textResult = await feature.TextHandler!
                    .HandleAsync(request.TextInput!, request, progress, cancellationToken)
                    .ConfigureAwait(false);
                result = new AnalysisResult(textResult with { CaptureSourceKind = "none" });
            }
            else
            {
                stage = ScanStage.Capture;
                await RenderProgressAsync(
                    request,
                    stage,
                    "VRChatの表示を1回だけ取得しています…",
                    total.Elapsed,
                    cancellationToken).ConfigureAwait(false);

                operation.ThrowIfNotCurrent();
                Stopwatch captureTimer = Stopwatch.StartNew();
                using CapturedFrame frame = await _captureSource
                    .CaptureAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                operation.ThrowIfNotCurrent();
                captureTimer.Stop();
                _logger.Info(
                    "capture.completed",
                    request.CorrelationId,
                    ScanStage.Capture,
                    captureTimer.Elapsed,
                    new Dictionary<string, long>
                    {
                        ["width"] = frame.Width,
                        ["height"] = frame.Height,
                        ["encodedBytes"] = frame.EncodedImage.Length,
                    });

                stage = ScanStage.Ocr;
                AnalysisResult analysisResult = await feature.Analyzer!
                    .AnalyzeAsync(frame, request, progress, cancellationToken)
                    .ConfigureAwait(false);
                result = analysisResult with { CaptureSourceKind = frame.SourceKind };
            }

            operation.ThrowIfNotCurrent();
            if (result.FeatureId != feature.Descriptor.Id)
            {
                throw new InvalidOperationException(
                    "The handler returned a result for a different feature ID.");
            }

            total.Stop();
            ScanOutcome success = ScanOutcome.Succeeded(
                request.CorrelationId,
                result,
                total.Elapsed);

            _logger.Info(
                "scan.completed",
                request.CorrelationId,
                ScanStage.Completed,
                total.Elapsed,
                new Dictionary<string, long>
                {
                    ["resultSections"] = result.Sections.Count,
                    ["primaryCharacters"] = result.PrimarySection.Text.Length,
                });

            stage = ScanStage.Rendering;
            await _renderer.RenderOutcomeAsync(success, cancellationToken).ConfigureAwait(false);
            operation.ThrowIfNotCurrent();
            return success;
        }
        // A terminal cleanup failure stops admissions and invalidates this operation by design.
        // Preserve its restart-required code; ordinary stale/cancelled failures remain cancellation.
        catch (Exception exception) when ((cancellationToken.IsCancellationRequested || !operation.IsCurrent)
            && exception is not ScanException { FailureCode: ScanFailureCode.VideoSearchCleanupFailed })
        {
            total.Stop();
            ScanOutcome cancelled = ScanOutcome.Failed(
                request.CorrelationId,
                new ScanFailure(
                    ScanFailureCode.Cancelled,
                    stage,
                    "SCANをキャンセルしました。"),
                total.Elapsed);
            _logger.Error(
                "scan.cancelled",
                request.CorrelationId,
                stage,
                ScanFailureCode.Cancelled,
                exception);
            // The owner presents cancellation only after cleanup; this generation is invalid.
            return cancelled;
        }
        catch (ScanException exception)
        {
            total.Stop();
            ScanOutcome failed = ScanOutcome.Failed(
                request.CorrelationId,
                new ScanFailure(exception.FailureCode, exception.Stage, exception.UserMessage),
                total.Elapsed);
            _logger.Error(
                "scan.failed",
                request.CorrelationId,
                exception.Stage,
                exception.FailureCode,
                exception);
            if (operation.IsCurrent)
            {
                await _renderer.RenderOutcomeAsync(failed, CancellationToken.None).ConfigureAwait(false);
            }
            return failed;
        }
        catch (Exception exception)
        {
            total.Stop();
            ScanOutcome failed = ScanOutcome.Failed(
                request.CorrelationId,
                new ScanFailure(
                    ScanFailureCode.Unexpected,
                    stage,
                    "予期しないエラーが発生しました。ログの相関IDを確認してください。"),
                total.Elapsed);
            _logger.Error(
                "scan.unexpected",
                request.CorrelationId,
                stage,
                ScanFailureCode.Unexpected,
                exception);
            if (operation.IsCurrent)
            {
                await _renderer.RenderOutcomeAsync(failed, CancellationToken.None).ConfigureAwait(false);
            }
            return failed;
        }
    }

    private async Task RenderProgressAsync(
        ScanRequest request,
        ScanStage stage,
        string message,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        _logger.Info("scan.progress", request.CorrelationId, stage, elapsed);
        await _renderer
            .RenderProgressAsync(
                new ScanProgress(request.CorrelationId, stage, message, elapsed),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

internal sealed class NullPrivacySafeLogger : IPrivacySafeLogger
{
    public static NullPrivacySafeLogger Instance { get; } = new();

    private NullPrivacySafeLogger()
    {
    }

    public void Info(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        TimeSpan? duration = null,
        IReadOnlyDictionary<string, long>? numericMetrics = null)
    {
    }

    public void Error(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        ScanFailureCode failureCode,
        Exception exception)
    {
    }
}
