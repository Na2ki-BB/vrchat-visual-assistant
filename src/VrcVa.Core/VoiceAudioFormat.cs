namespace VrcVa.Core;

/// <summary>
/// The initial in-memory recording format: little-endian PCM16, mono, 16 kHz.
/// WAV headers never contribute to the audio-duration quota.
/// </summary>
public static class VoiceAudioFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BlockAlignment = Channels * BitsPerSample / 8;
    public const int BytesPerSecond = SampleRate * BlockAlignment;
    public const int WaveHeaderBytes = 44;
    public const int MaximumPcmDataBytes =
        VoiceInputOptions.MaximumAllowedRecordingSeconds * BytesPerSecond;
    public const int MaximumWaveBytes = MaximumPcmDataBytes + WaveHeaderBytes;

    public static int GetPcmByteLimit(int maximumRecordingSeconds)
    {
        if (maximumRecordingSeconds is < 1 or > VoiceInputOptions.MaximumAllowedRecordingSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecordingSeconds));
        }

        return maximumRecordingSeconds * BytesPerSecond;
    }

    /// <summary>
    /// Reserves whole seconds, rounded up separately for each transmission.
    /// Duration comes from captured sample bytes, not a UI timer or file length.
    /// </summary>
    public static int GetQuotaSeconds(int pcmDataBytes)
    {
        if (pcmDataBytes <= 0
            || pcmDataBytes > MaximumPcmDataBytes
            || pcmDataBytes % BlockAlignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pcmDataBytes));
        }

        return 1 + ((pcmDataBytes - 1) / BytesPerSecond);
    }
}
