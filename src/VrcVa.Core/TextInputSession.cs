using System.Text;

namespace VrcVa.Core;

/// <summary>
/// One completed, in-memory transcript shared by feature-specific operations.
/// Search queries and candidates belong to those operations and cannot replace it.
/// Session invalidation and cancellation are owned by the execution lifecycle.
/// </summary>
public sealed class TextInputSession
{
    public const int MaximumTranscriptUtf8Bytes = 4_000;

    public TextInputSession(Guid sessionId, string transcript)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        if (Encoding.UTF8.GetByteCount(transcript) > MaximumTranscriptUtf8Bytes)
        {
            throw new ArgumentException("The transcript exceeds the input limit.", nameof(transcript));
        }

        SessionId = sessionId;
        Transcript = transcript;
    }

    public Guid SessionId { get; }

    public string Transcript { get; }

    public static TextInputSession Create(string transcript) => new(Guid.NewGuid(), transcript);
}
