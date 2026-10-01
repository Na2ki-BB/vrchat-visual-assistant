using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Security;
using VrcVa.Windows.Settings;

namespace VrcVa.Windows.Voice;

internal interface IVoiceCredentialStore
{
    string? Read();
    void Write(string secret);
    void Delete();
}

/// <summary>The only production voice store; translation and environment credentials are never consulted.</summary>
internal sealed class WindowsVoiceCredentialStore : IVoiceCredentialStore
{
    private readonly WindowsCredentialStore _store = new(OpenAiVoiceCredential.CredentialTarget);
    public string? Read() => _store.Read();
    public void Write(string secret) => _store.Write(secret);
    public void Delete() => _store.Delete();
}

/// <summary>Called under the app's configuration lease. Persists only the opt-in preference.</summary>
internal sealed class VoiceInputConfiguration(
    Func<VrcVaSettings> loadSettings,
    Action<VrcVaSettings> saveSettings,
    IVoiceCredentialStore credentials,
    VoiceRequestQuota quota,
    Func<VoiceInputOptions, OpenAiVoiceCredential, IVoiceTranscriber>? createTranscriber = null)
{
    public VoiceInputRuntime CreateRuntime()
    {
        VoiceInputOptions options = loadSettings().VoiceInput;
        options.Validate();
        if (!options.IsEnabled) { throw new VoiceInputException(VoiceInputFailureCode.Disabled); }
        OpenAiVoiceCredential credential = new(credentials.Read());
        if (!credential.IsAvailable) { throw new VoiceInputException(VoiceInputFailureCode.VoiceKeyUnavailable); }
        IVoiceTranscriber transcriber = createTranscriber?.Invoke(options, credential)
            ?? new OpenAiVoiceTranscriber(new(), options, credential, quota);
        return new(options, transcriber);
    }

    public void SetEnabled(bool enabled)
    {
        VrcVaSettings settings = loadSettings();
        saveSettings(settings with { VoiceInput = settings.VoiceInput with { IsEnabled = enabled } });
    }

    public void SaveKey(string secret)
    {
        OpenAiVoiceCredential credential = new(secret);
        if (!credential.IsAvailable) { throw new ArgumentException("A voice credential is required."); }
        credentials.Write(secret);
    }

    public void DeleteKey() => credentials.Delete();
}

internal sealed class VoiceInputRuntime(VoiceInputOptions options, IVoiceTranscriber transcriber) : IDisposable
{
    private int _disposed;
    public VoiceInputOptions Options { get; } = options;
    public IVoiceTranscriber Transcriber { get; } = transcriber;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { (Transcriber as IDisposable)?.Dispose(); }
    }
}
