using VrcVa.Core;

namespace VrcVa.Windows.Translation;

internal sealed class ReloadableAnalyzer : IAnalyzer
{
    private TranslationRuntime _runtime;

    public ReloadableAnalyzer(TranslationRuntime initialRuntime)
    {
        ArgumentNullException.ThrowIfNull(initialRuntime);
        _runtime = initialRuntime;
    }

    public TranslationRuntime Current => Volatile.Read(ref _runtime);

    public void Swap(TranslationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Volatile.Write(ref _runtime, runtime);
    }

    public Task<AnalysisResult> AnalyzeAsync(
        CapturedFrame frame,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        TranslationRuntime snapshot = Current;
        return snapshot.Analyzer.AnalyzeAsync(frame, request, progress, cancellationToken);
    }
}
