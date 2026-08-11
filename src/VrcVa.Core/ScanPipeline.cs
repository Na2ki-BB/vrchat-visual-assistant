using System.Diagnostics;

namespace VrcVa.Core;

public sealed class ScanPipeline
{
    private readonly ICaptureSource _captureSource;
    private readonly IAnalyzer _analyzer;
    private readonly IResultRenderer _renderer;
    private readonly IPrivacySafeLogger _logger;
    private int _isRunning;

    public ScanPipeline(
        ICaptureSource captureSource,
        IAnalyzer analyzer,
        IResultRenderer renderer,
        IPrivacySafeLogger? logger = null)
    {
        _captureSource = captureSource;
        _analyzer = analyzer;
        _renderer = renderer;
        _logger = logger ?? NullPrivacySafeLogger.Instance;
    }

    public async Task<ScanOutcome> RunAsync(
        ScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            ScanOutcome busy = ScanOutcome.Failed(
                request.CorrelationId,
                new ScanFailure(
                    ScanFailureCode.Busy,
                    ScanStage.Trigger,
                    "前のSCANを処理中です。完了してからもう一度お試しください。"),
                TimeSpan.Zero);
            await _renderer.RenderOutcomeAsync(busy, cancellationToken).ConfigureAwait(false);
            return busy;
        }

        Stopwatch total = Stopwatch.StartNew();
        ScanStage stage = ScanStage.Trigger;

        try
        {
            await RenderProgressAsync(
                request,
                ScanStage.Trigger,
                "SCANを開始しました。",
                total.Elapsed,
                cancellationToken).ConfigureAwait(false);

            stage = ScanStage.Capture;
            await RenderProgressAsync(
                request,
                stage,
                "VRChatの表示を1回だけ取得しています…",
                total.Elapsed,
                cancellationToken).ConfigureAwait(false);

            Stopwatch captureTimer = Stopwatch.StartNew();
            using CapturedFrame frame = await _captureSource
                .CaptureAsync(request, cancellationToken)
                .ConfigureAwait(false);
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

            InlineProgress<ScanProgress> progress = new(value =>
                _renderer
                    .RenderProgressAsync(value, cancellationToken)
                    .GetAwaiter()
                    .GetResult());

            stage = ScanStage.Ocr;
            AnalysisResult result = await _analyzer
                .AnalyzeAsync(frame, request, progress, cancellationToken)
                .ConfigureAwait(false);

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
                    ["sourceCharacters"] = result.SourceText.Length,
                    ["translatedCharacters"] = result.JapaneseText.Length,
                });

            stage = ScanStage.Rendering;
            await _renderer.RenderOutcomeAsync(success, cancellationToken).ConfigureAwait(false);
            return success;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
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
            await _renderer.RenderOutcomeAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
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
            await _renderer.RenderOutcomeAsync(failed, CancellationToken.None).ConfigureAwait(false);
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
            await _renderer.RenderOutcomeAsync(failed, CancellationToken.None).ConfigureAwait(false);
            return failed;
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
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
