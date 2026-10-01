using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>
/// Application-owned, independent audio, translation and interpretation counters.
/// Runtime/client replacement and settings reload preserve every counter identity.
/// </summary>
public sealed class FeatureUsageQuotas
{
    private readonly object _sync = new();
    private FeatureUsageLimits _limits;

    public FeatureUsageQuotas(FeatureUsageLimits? limits = null)
    {
        _limits = limits ?? new FeatureUsageLimits();
        _limits.Validate();
        Voice = new VoiceRequestQuota(_limits.VoiceSeconds, _limits.VoiceRequests, _sync);
        Translation = new TextRequestQuota(_limits.TranslationRequests,
            TextRequestPurpose.Translation, _sync);
        SearchInterpretation = new TextRequestQuota(_limits.SearchInterpretationRequests,
            TextRequestPurpose.SearchInterpretation, _sync);
    }

    public VoiceRequestQuota Voice { get; }

    public TextRequestQuota Translation { get; }

    public TextRequestQuota SearchInterpretation { get; }

    public FeatureUsageLimits Limits { get { lock (_sync) { return _limits; } } }

    /// <summary>
    /// Apply only between application operations. Validation is all-or-nothing;
    /// neither raising nor lowering a ceiling changes past consumption.
    /// </summary>
    public void ApplyLimits(FeatureUsageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        lock (_sync)
        {
            Voice.UpdateMaximums(limits.VoiceSeconds, limits.VoiceRequests);
            Translation.UpdateMaximum(limits.TranslationRequests);
            SearchInterpretation.UpdateMaximum(limits.SearchInterpretationRequests);
            _limits = limits;
        }
    }
}
