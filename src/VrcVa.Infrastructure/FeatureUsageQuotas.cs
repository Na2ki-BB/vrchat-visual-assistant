using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>
/// Application-owned, independent text counters. Audio counters are a separate
/// adapter; its configured ceilings are preserved here without reserving audio.
/// </summary>
public sealed class FeatureUsageQuotas
{
    private readonly object _sync = new();
    private FeatureUsageLimits _limits;

    public FeatureUsageQuotas(FeatureUsageLimits? limits = null)
    {
        _limits = limits ?? new FeatureUsageLimits();
        _limits.Validate();
        Translation = new TextRequestQuota(_limits.TranslationRequests,
            TextRequestPurpose.Translation, _sync);
        SearchInterpretation = new TextRequestQuota(_limits.SearchInterpretationRequests,
            TextRequestPurpose.SearchInterpretation, _sync);
    }

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
            Translation.UpdateMaximum(limits.TranslationRequests);
            SearchInterpretation.UpdateMaximum(limits.SearchInterpretationRequests);
            _limits = limits;
        }
    }
}
