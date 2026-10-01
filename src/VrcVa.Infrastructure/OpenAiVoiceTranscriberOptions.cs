namespace VrcVa.Infrastructure;

public sealed record OpenAiVoiceTranscriberOptions
{
    public const string SupportedModel = "gpt-transcribe";
    public const int MaximumResponseBytes = 64 * 1024;
    public const int MaximumTimeoutSeconds = 60;
    public static Uri OfficialEndpoint { get; } = new("https://api.openai.com/v1/audio/transcriptions");

    public string Model { get; init; } = SupportedModel;
    public Uri Endpoint { get; init; } = OfficialEndpoint;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(MaximumTimeoutSeconds);
    public int ResponseByteLimit { get; init; } = MaximumResponseBytes;

    public void Validate()
    {
        if (Model != SupportedModel || Endpoint is null || !Endpoint.IsAbsoluteUri
            || !string.Equals(Endpoint.AbsoluteUri, OfficialEndpoint.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new ArgumentException("Voice transcription requires the approved model and official endpoint.");
        }

        if (Timeout <= TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        if (ResponseByteLimit is < 1 or > MaximumResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ResponseByteLimit));
        }
    }
}
