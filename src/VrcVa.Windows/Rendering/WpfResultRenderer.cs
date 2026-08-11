using System.Windows.Threading;
using VrcVa.Core;

namespace VrcVa.Windows.Rendering;

internal sealed class WpfResultRenderer(
    Dispatcher dispatcher,
    Action<ScanProgress> progressAction,
    Action<ScanOutcome> outcomeAction) : IResultRenderer
{
    public Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken) =>
        InvokeAsync(() => progressAction(progress), cancellationToken);

    public Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken) =>
        InvokeAsync(() => outcomeAction(outcome), cancellationToken);

    private Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }
}
