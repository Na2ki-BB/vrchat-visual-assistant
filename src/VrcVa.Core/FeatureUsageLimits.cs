namespace VrcVa.Core;

/// <summary>
/// Independent per-process usage ceilings, not usage counters or account budgets.
/// The runtime quota adapters will consume these limits in later implementation tasks.
/// </summary>
public sealed record FeatureUsageLimits
{
    public const int DefaultVoiceSeconds = 300;
    public const int DefaultVoiceRequests = 30;
    public const int DefaultTextRequests = 10;
    public const int MaximumAllowedVoiceSeconds = 3_600;
    public const int MaximumAllowedVoiceRequests = 300;
    public const int MaximumAllowedTextRequests = 100;

    public int VoiceSeconds { get; init; } = DefaultVoiceSeconds;

    public int VoiceRequests { get; init; } = DefaultVoiceRequests;

    public int TranslationRequests { get; init; } = DefaultTextRequests;

    public int SearchInterpretationRequests { get; init; } = DefaultTextRequests;

    public void Validate()
    {
        ValidateLimit(VoiceSeconds, MaximumAllowedVoiceSeconds, nameof(VoiceSeconds));
        ValidateLimit(VoiceRequests, MaximumAllowedVoiceRequests, nameof(VoiceRequests));
        ValidateLimit(TranslationRequests, MaximumAllowedTextRequests, nameof(TranslationRequests));
        ValidateLimit(SearchInterpretationRequests, MaximumAllowedTextRequests, nameof(SearchInterpretationRequests));
    }

    private static void ValidateLimit(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Usage limit must be between 1 and {maximum}.");
        }
    }
}
