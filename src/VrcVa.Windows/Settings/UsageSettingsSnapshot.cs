using VrcVa.Core;
using VrcVa.Infrastructure;

namespace VrcVa.Windows.Settings;

/// <summary>
/// Validated preferences, separate from process-lifetime counters. A failed read or
/// validation cannot replace the last good snapshot or partially change a ceiling.
/// The caller holds the application's execution gate while applying a reload.
/// </summary>
internal sealed class UsageSettingsSnapshot
{
    private readonly FeatureUsageQuotas _quotas;
    private bool _hasStoredSettings;

    public UsageSettingsSnapshot(FeatureUsageQuotas quotas)
    {
        ArgumentNullException.ThrowIfNull(quotas);
        _quotas = quotas;
    }

    public VoiceInputOptions VoiceInput { get; private set; } = new();

    public FeatureUsageLimits UsageLimits => _quotas.Limits;

    public VrcVaSettings Reload(VrcVaSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        bool hasStoredSettings = false;
        VrcVaSettings settings;
        try
        {
            settings = store.Load(allowMissing: !_hasStoredSettings, out hasStoredSettings);
        }
        finally
        {
            // A previously observed file may not disappear into a larger default
            // allowance, even when the observed file's contents were invalid.
            _hasStoredSettings |= hasStoredSettings;
        }

        Apply(settings);
        return settings;
    }

    public void Apply(VrcVaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _quotas.ApplyLimits(settings.UsageLimits);
        VoiceInput = settings.VoiceInput;
    }
}
