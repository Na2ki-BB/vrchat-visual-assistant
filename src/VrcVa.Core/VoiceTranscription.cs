namespace VrcVa.Core;

/// <summary>
/// Transcribes one borrowed, canonical in-memory WAV. The caller holds its audio
/// lease until completion or cancellation cleanup and does not mutate the buffer;
/// this adapter never owns it.
/// </summary>
public interface IVoiceTranscriber
{
    Task<VoiceTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> waveBytes,
        CancellationToken cancellationToken);
}

public sealed record VoiceTranscription(string Text, string Provider, string Model);
