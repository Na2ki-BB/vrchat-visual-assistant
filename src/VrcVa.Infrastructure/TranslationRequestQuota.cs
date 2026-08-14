namespace VrcVa.Infrastructure;

/// <summary>
/// Tracks OpenAI text-model request attempts across client instances in one process.
/// </summary>
public sealed class TranslationRequestQuota
{
    public const int HardMaximum = OpenAiTranslatorOptions.DefaultMaxRequestsPerSession;

    private int _requestAttempts;

    public TranslationRequestQuota(int maximum = HardMaximum)
    {
        if (maximum is < 1 or > HardMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximum),
                maximum,
                $"Translation request maximum must be between 1 and {HardMaximum}.");
        }

        Maximum = maximum;
    }

    public int Maximum { get; }

    public int Remaining => Math.Max(0, Maximum - Volatile.Read(ref _requestAttempts));

    internal bool TryReserve()
    {
        while (true)
        {
            int current = Volatile.Read(ref _requestAttempts);
            if (current >= Maximum)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _requestAttempts, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
