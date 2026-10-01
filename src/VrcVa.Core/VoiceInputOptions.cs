namespace VrcVa.Core;

/// <summary>
/// Non-secret preferences for the shared voice-input adapter. These options do
/// not enable a microphone or a provider until the Windows flow is connected.
/// </summary>
public sealed record VoiceInputOptions
{
    public const int DefaultMaximumRecordingSeconds = 30;
    public const int MaximumAllowedRecordingSeconds = 120;
    public const int DefaultFailedAudioRetentionSeconds = 120;
    public const int MinimumFailedAudioRetentionSeconds = 15;
    public const int MaximumFailedAudioRetentionSeconds = 300;

    public bool IsEnabled { get; init; }

    public int MaximumRecordingSeconds { get; init; } = DefaultMaximumRecordingSeconds;

    public int FailedAudioRetentionSeconds { get; init; } = DefaultFailedAudioRetentionSeconds;

    public void Validate()
    {
        if (MaximumRecordingSeconds is < 1 or > MaximumAllowedRecordingSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRecordingSeconds),
                $"Recording duration must be between 1 and {MaximumAllowedRecordingSeconds} seconds.");
        }

        if (FailedAudioRetentionSeconds is < MinimumFailedAudioRetentionSeconds
            or > MaximumFailedAudioRetentionSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FailedAudioRetentionSeconds),
                $"Failed audio retention must be between {MinimumFailedAudioRetentionSeconds} and {MaximumFailedAudioRetentionSeconds} seconds.");
        }
    }
}
