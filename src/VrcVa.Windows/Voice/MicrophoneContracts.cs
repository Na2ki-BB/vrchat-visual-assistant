namespace VrcVa.Windows.Voice;

internal interface IMicrophoneFactory
{
    Task<IMicrophone> OpenAsync(CancellationToken cancellationToken);
}

internal interface IMicrophone : IAsyncDisposable
{
    // Synchronous, borrowed PCM16 slices. Complete only after final buffers are drained.
    Task RecordAsync(Action<ReadOnlyMemory<byte>> receiveSamples, CancellationToken stopToken);
}

internal enum VoiceInputFailureCode
{
    Disabled,
    VoiceKeyUnavailable,
    Busy,
    DeviceUnavailable,
    FormatUnsupported,
    MicrophoneAccessDenied,
    DeviceLost,
    RecordingFailed,
    MicrophoneCleanupFailed,
    EmptyAudio,
    SilentAudio,
    InvalidAudio,
    InputProcessingFailed,
}

internal sealed class VoiceInputException(VoiceInputFailureCode code) : Exception(code.ToString())
{
    public VoiceInputFailureCode Code { get; } = code;
}
