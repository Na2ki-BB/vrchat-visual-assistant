namespace VrcVa.Core.Tests;

public sealed class VoiceInputOptionsTests
{
    [Fact]
    public void Defaults_KeepVoiceDisabledAndApprovedLimits()
    {
        VoiceInputOptions voice = new();
        FeatureUsageLimits usage = new();

        voice.Validate();
        usage.Validate();

        Assert.False(voice.IsEnabled);
        Assert.Equal(30, voice.MaximumRecordingSeconds);
        Assert.Equal(120, voice.FailedAudioRetentionSeconds);
        Assert.Equal(300, usage.VoiceSeconds);
        Assert.Equal(30, usage.VoiceRequests);
        Assert.Equal(10, usage.TranslationRequests);
        Assert.Equal(10, usage.SearchInterpretationRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    public void RecordingLimit_AcceptsInclusiveRange(int seconds)
    {
        VoiceInputOptions options = new() { MaximumRecordingSeconds = seconds };
        options.Validate();
        Assert.Equal(seconds * 32_000, VoiceAudioFormat.GetPcmByteLimit(seconds));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    public void RecordingLimit_RejectsOutsideRange(int seconds)
    {
        VoiceInputOptions options = new() { MaximumRecordingSeconds = seconds };
        Assert.Equal(
            nameof(VoiceInputOptions.MaximumRecordingSeconds),
            Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAudioFormat.GetPcmByteLimit(seconds));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(120)]
    [InlineData(300)]
    public void FailedAudioRetention_AcceptsInclusiveRange(int seconds)
    {
        new VoiceInputOptions { FailedAudioRetentionSeconds = seconds }.Validate();
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(301)]
    [InlineData(int.MaxValue)]
    public void FailedAudioRetention_RejectsOutsideRange(int seconds)
    {
        VoiceInputOptions options = new() { FailedAudioRetentionSeconds = seconds };
        Assert.Equal(
            nameof(VoiceInputOptions.FailedAudioRetentionSeconds),
            Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);
    }

    [Theory]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds))]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests))]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests))]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests))]
    public void UsageLimits_CanBeChangedIndependently(string field)
    {
        FeatureUsageLimits defaults = new();
        FeatureUsageLimits changed = field switch
        {
            nameof(FeatureUsageLimits.VoiceSeconds) => defaults with { VoiceSeconds = 25 },
            nameof(FeatureUsageLimits.VoiceRequests) => defaults with { VoiceRequests = 25 },
            nameof(FeatureUsageLimits.TranslationRequests) => defaults with { TranslationRequests = 25 },
            nameof(FeatureUsageLimits.SearchInterpretationRequests) => defaults with { SearchInterpretationRequests = 25 },
            _ => throw new ArgumentException("Unknown test field.", nameof(field)),
        };

        changed.Validate();

        Assert.Equal(field == nameof(FeatureUsageLimits.VoiceSeconds) ? 25 : 300, changed.VoiceSeconds);
        Assert.Equal(field == nameof(FeatureUsageLimits.VoiceRequests) ? 25 : 30, changed.VoiceRequests);
        Assert.Equal(field == nameof(FeatureUsageLimits.TranslationRequests) ? 25 : 10, changed.TranslationRequests);
        Assert.Equal(field == nameof(FeatureUsageLimits.SearchInterpretationRequests) ? 25 : 10, changed.SearchInterpretationRequests);
        Assert.Equal(new FeatureUsageLimits(), defaults);
    }

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(3_600, 300, 100, 100)]
    public void UsageLimits_AcceptInclusiveRanges(int seconds, int voice, int translation, int interpretation)
    {
        new FeatureUsageLimits
        {
            VoiceSeconds = seconds,
            VoiceRequests = voice,
            TranslationRequests = translation,
            SearchInterpretationRequests = interpretation,
        }.Validate();
    }

    [Theory]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), int.MinValue)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), -1)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), 0)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), 3_601)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), int.MinValue)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), 301)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), int.MinValue)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), 101)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), int.MinValue)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), 101)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), int.MaxValue)]
    public void UsageLimits_RejectEachInvalidField(string field, int value)
    {
        FeatureUsageLimits defaults = new();
        FeatureUsageLimits options = field switch
        {
            nameof(FeatureUsageLimits.VoiceSeconds) => defaults with { VoiceSeconds = value },
            nameof(FeatureUsageLimits.VoiceRequests) => defaults with { VoiceRequests = value },
            nameof(FeatureUsageLimits.TranslationRequests) => defaults with { TranslationRequests = value },
            nameof(FeatureUsageLimits.SearchInterpretationRequests) => defaults with { SearchInterpretationRequests = value },
            _ => throw new ArgumentException("Unknown test field.", nameof(field)),
        };

        Assert.Equal(field, Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);
    }

    [Fact]
    public void WaveSize_IsBoundedSeparatelyFromTheHeaderAndQuota()
    {
        Assert.Equal(16_000, VoiceAudioFormat.SampleRate);
        Assert.Equal(1, VoiceAudioFormat.Channels);
        Assert.Equal(16, VoiceAudioFormat.BitsPerSample);
        Assert.Equal(2, VoiceAudioFormat.BlockAlignment);
        Assert.Equal(3_840_000, VoiceAudioFormat.MaximumPcmDataBytes);
        Assert.Equal(3_840_044, VoiceAudioFormat.MaximumWaveBytes);
        Assert.Equal(960_000, VoiceAudioFormat.GetPcmByteLimit(30));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(31_998, 1)]
    [InlineData(32_000, 1)]
    [InlineData(32_002, 2)]
    [InlineData(959_998, 30)]
    [InlineData(960_000, 30)]
    [InlineData(960_002, 31)]
    [InlineData(3_840_000, 120)]
    public void QuotaSeconds_RoundUpActualSampleBytes(int bytes, int expected)
    {
        Assert.Equal(expected, VoiceAudioFormat.GetQuotaSeconds(bytes));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31_999)]
    [InlineData(3_840_002)]
    [InlineData(int.MaxValue)]
    public void QuotaSeconds_RejectEmptyUnalignedOrOversizedAudio(int bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAudioFormat.GetQuotaSeconds(bytes));
    }
}
