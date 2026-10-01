namespace VrcVa.Infrastructure;

/// <summary>
/// Explicit audio-only credential snapshot. J3 must read only this dedicated
/// Windows Credential Manager target, never a translation or environment key.
/// No credential store is read or written by this type or the HTTP adapter.
/// </summary>
public sealed class OpenAiVoiceCredential
{
    public const string CredentialTarget = "VrcVa/OpenAI/Voice";

    public OpenAiVoiceCredential(string? apiKey)
    {
        string? value = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (value is not null && value.Any(char.IsControl))
        {
            throw new ArgumentException("The voice credential contains invalid characters.", nameof(apiKey));
        }

        ApiKey = value;
    }

    internal string? ApiKey { get; }

    public bool IsAvailable => ApiKey is not null;

    public override string ToString() => "OpenAI voice credential (redacted)";
}
