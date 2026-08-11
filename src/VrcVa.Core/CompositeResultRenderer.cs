namespace VrcVa.Core;

public sealed class CompositeResultRenderer(params IResultRenderer[] renderers) : IResultRenderer
{
    private readonly IReadOnlyList<IResultRenderer> _renderers =
        renderers.Length == 0
            ? throw new ArgumentException("At least one renderer is required.", nameof(renderers))
            : renderers;

    public async Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        foreach (IResultRenderer renderer in _renderers)
        {
            await renderer.RenderProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        foreach (IResultRenderer renderer in _renderers)
        {
            await renderer.RenderOutcomeAsync(outcome, cancellationToken).ConfigureAwait(false);
        }
    }
}
