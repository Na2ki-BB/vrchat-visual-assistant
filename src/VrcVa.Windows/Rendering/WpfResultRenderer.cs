using System.Windows.Threading;
using VrcVa.Core;

namespace VrcVa.Windows.Rendering;

internal sealed class WpfResultRenderer(
    Dispatcher dispatcher,
    Action<ScanProgress> progressAction,
    Action<ScanOutcome> outcomeAction,
    Func<Guid, bool>? canRender = null) : IResultRenderer
{
    public Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken) =>
        InvokeAsync(progress.CorrelationId, () => progressAction(progress), cancellationToken);

    public Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken) =>
        InvokeAsync(outcome.CorrelationId, () => outcomeAction(outcome), cancellationToken);

    private Task InvokeAsync(Guid operationId, Action action, CancellationToken cancellationToken)
    {
        void RenderIfCurrent()
        {
            if (cancellationToken.IsCancellationRequested || canRender?.Invoke(operationId) == false)
            {
                return;
            }

            action();
        }

        if (dispatcher.CheckAccess())
        {
            RenderIfCurrent();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            RenderIfCurrent,
            DispatcherPriority.Normal).Task;
    }
}
