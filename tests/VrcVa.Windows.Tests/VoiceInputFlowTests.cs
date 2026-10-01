using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Settings;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VoiceInputFlowTests
{
    [Fact]
    public async Task Success_UsesDedicatedCredentialAndAppQuota_DisposesAudioBeforeIdle_ExposesFullSharedText()
    {
        await using VoiceFlowFixture fixture = new();
        string full = "  架空の認識文\n全文は変更しない  ";
        fixture.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success(full));
        await fixture.RecordAsync();
        Assert.Equal(VoiceFlowState.Completed, fixture.Flow.State);
        Assert.Equal(full, fixture.Flow.CurrentInput!.Transcript);
        Assert.Equal(fixture.Execution.CurrentSessionId, fixture.Flow.CurrentInput.SessionId);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(1, fixture.Quotas.Voice.ConsumedRequests);
        Assert.Equal(1, fixture.Quotas.Voice.ConsumedSeconds);
        Assert.Equal(0, fixture.Quotas.Translation.Consumed);
        Assert.Equal(0, fixture.Quotas.SearchInterpretation.Consumed);
        Assert.True(fixture.Microphone.Disposed);
        Assert.False(fixture.Execution.IsRunning);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
        Assert.False(fixture.Flow.CanRetry);
        Assert.Empty(fixture.SavedSettings);
    }

    [Fact]
    public async Task StoredKeyWithoutExplicitOptIn_NeverReadsKeyOpensMicrophoneOrSends()
    {
        await using VoiceFlowFixture fixture = new(enabled: false);
        Assert.False(fixture.Flow.TryStart());
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Opens);
        Assert.Equal(0, fixture.Requests);
        Assert.Equal(VoiceInputFailureCode.Disabled.ToString(), fixture.Flow.FailureCode);
        Assert.False(fixture.Execution.IsRunning);
    }

    [Fact]
    public async Task ConsentAndKeySavingAreIndependent_OnlyConsentPersists_NoContentsInSettings()
    {
        await using VoiceFlowFixture fixture = new(enabled: false);
        fixture.Configuration.SaveKey("fake-secret-only");
        Assert.False(fixture.Settings.VoiceInput.IsEnabled);
        Assert.Empty(fixture.SavedSettings);
        fixture.Configuration.SetEnabled(true);
        Assert.Single(fixture.SavedSettings);
        fixture.Credentials.Value = null;
        Assert.False(fixture.Flow.TryStart());
        Assert.Equal(0, fixture.Opens);
        fixture.Configuration.SaveKey("fake-secret-only");
        await fixture.RecordAsync();
        string saved = string.Join("", fixture.SavedSettings);
        Assert.DoesNotContain("fake-secret-only", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic transcript", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("RIFF", saved, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Credentials.Writes);
    }

    [Fact]
    public async Task FailedManualRetry_UsesSameAudioWithoutRecording_ConsumesNewQuota_AndNeverExtendsDeadline()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.RecordAsync();
        Assert.Equal(VoiceFlowState.Failed, fixture.Flow.State);
        Assert.True(fixture.Flow.CanRetry);
        Assert.Equal(1, fixture.Requests);
        Assert.Contains(fixture.BorrowedAudio.ToArray(), b => b != 0);
        fixture.Time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(fixture.Flow.TryRetry());
        await fixture.Flow.WhenIdle;
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(2, fixture.Quotas.Voice.ConsumedRequests);
        Assert.Equal(1, fixture.Opens);
        Assert.True(fixture.Flow.CanRetry);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fixture.Flow.Refresh();
        Assert.False(fixture.Flow.CanRetry);
        Assert.False(fixture.Flow.TryRetry());
        Assert.Equal(2, fixture.Requests);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task RetryAdmittedBeforeExpiry_CanFinish_ThenAudioIsZeroed()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.RecordAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { started.SetResult(); return response.Task; };
        fixture.Time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(fixture.Flow.TryRetry());
        await started.Task;
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains(fixture.BorrowedAudio.ToArray(), b => b != 0);
        response.SetResult(VoiceFlowFixture.Success("期限内に開始した認識文"));
        await fixture.Flow.WhenIdle;
        Assert.Equal("期限内に開始した認識文", fixture.Flow.CurrentInput!.Transcript);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("rerecord")]
    [InlineData("new-scan")]
    [InlineData("shutdown")]
    public async Task FailedAudioIsDiscardedByEveryExit(string action)
    {
        VoiceFlowFixture fixture = new();
        await using (fixture)
        {
            fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            await fixture.RecordAsync();
            ReadOnlyMemory<byte> oldAudio = fixture.BorrowedAudio;
            switch (action)
            {
                case "close": await fixture.Flow.CloseAsync(); break;
                case "cancel": fixture.Flow.Cancel(); break;
                case "rerecord":
                    fixture.NextMicrophone();
                    Assert.True(fixture.Flow.TryStart());
                    await fixture.Microphone.Started.Task;
                    fixture.Flow.Cancel();
                    await fixture.Flow.WhenIdle;
                    break;
                case "new-scan":
                    Assert.True(fixture.Execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
                    await fixture.Flow.CloseAsync();
                    Assert.True(scan!.IsCurrent);
                    scan.Dispose();
                    break;
                case "shutdown": await fixture.Flow.DisposeAsync(); break;
            }
            Assert.Null(fixture.Flow.CurrentInput);
            Assert.False(fixture.Flow.CanRetry);
            Assert.All(oldAudio.ToArray(), b => Assert.Equal(0, b));
            Assert.Equal(1, fixture.Requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrCloseDuringLateTranscription_HoldsGateUntilCleanup_RejectsResponse(bool close)
    {
        await using VoiceFlowFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { started.SetResult(); return response.Task; };
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        fixture.Flow.StopRecording();
        await started.Task;
        Task closing = Task.CompletedTask;
        if (close) { closing = fixture.Flow.CloseAsync(); }
        else { fixture.Flow.Cancel(); }
        Assert.True(fixture.Execution.IsRunning);
        Assert.False(fixture.Flow.TryStart());
        Assert.Null(fixture.Flow.CurrentInput);
        Assert.Contains(fixture.BorrowedAudio.ToArray(), b => b != 0);
        response.SetResult(VoiceFlowFixture.Success("must not render"));
        await fixture.Flow.WhenIdle;
        await closing;
        Assert.False(fixture.Execution.IsRunning);
        Assert.Null(fixture.Flow.CurrentInput);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(1, fixture.Quotas.Voice.ConsumedRequests);
    }

    [Fact]
    public async Task CancelRecording_DrainsMicrophoneBeforeReleasingGate_AndNeverTranscribes()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Microphone.DelayCleanup = true;
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        fixture.Flow.Cancel();
        await fixture.Microphone.CleanupStarted.Task;
        Assert.True(fixture.Execution.IsRunning);
        Assert.False(fixture.Flow.TryStart());
        Assert.Equal(0, fixture.Requests);
        fixture.Microphone.Cleanup.SetResult();
        await fixture.Flow.WhenIdle;
        Assert.False(fixture.Execution.IsRunning);
        Assert.Equal(0, fixture.Quotas.Voice.ConsumedRequests);
    }

    [Fact]
    public async Task BusyScanPreservesTranscript_AndRerecordReplacesRatherThanAppends()
    {
        await using VoiceFlowFixture fixture = new();
        await fixture.RecordAsync();
        TextInputSession original = fixture.Flow.CurrentInput!;
        Assert.True(fixture.Execution.TryBeginOperation(original.SessionId, Guid.NewGuid(), out ExecutionOperation? other));
        Assert.False(fixture.Flow.TryStart());
        Assert.Same(original, fixture.Flow.CurrentInput);
        other!.Dispose();
        fixture.NextMicrophone();
        fixture.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success("new text"));
        await fixture.RecordAsync();
        Assert.NotEqual(original.SessionId, fixture.Flow.CurrentInput!.SessionId);
        Assert.Equal("new text", fixture.Flow.CurrentInput.Transcript);
        Assert.Equal(2, fixture.Quotas.Voice.ConsumedRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "VoiceAuthenticationFailed")]
    [InlineData(HttpStatusCode.TooManyRequests, "VoiceRateLimited")]
    [InlineData(HttpStatusCode.BadGateway, "VoiceTranscriptionFailed")]
    public async Task TypedHttpFailure_DoesNotExposeResponseBodyOrAutoRetry(HttpStatusCode status, string code)
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("private audio key transcript must not appear"),
        });
        await fixture.RecordAsync();
        Assert.Equal(code, fixture.Flow.FailureCode);
        Assert.DoesNotContain("private", fixture.Flow.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(status != HttpStatusCode.Unauthorized, fixture.Flow.CanRetry);
    }

    [Fact]
    public async Task CancelledRetryWithLateTransportFailureFinishesCancelledAfterCleanup()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.RecordAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { started.SetResult(); return late.Task; };
        Assert.True(fixture.Flow.TryRetry());
        await started.Task;
        fixture.Flow.Cancel();
        Assert.True(fixture.Execution.IsRunning);
        late.SetException(new HttpRequestException("private diagnostic"));
        await fixture.Flow.WhenIdle;
        Assert.Equal(VoiceFlowState.Cancelled, fixture.Flow.State);
        Assert.False(fixture.Execution.IsRunning);
        Assert.Null(fixture.Flow.CurrentInput);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task NativeCleanupFailure_StopsAllWorkAndSurfacesTypedRestartGuidance()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Microphone.FailCleanup = true;
        await fixture.RecordAsync();
        Assert.Equal(VoiceFlowState.Failed, fixture.Flow.State);
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed.ToString(), fixture.Flow.FailureCode);
        Assert.True(fixture.Flow.RequiresRestart);
        Assert.Contains("アプリを終了", fixture.Flow.Message, StringComparison.Ordinal);
        Assert.Null(fixture.Flow.CurrentInput);
        Assert.False(fixture.Flow.TryStart());
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task InvalidSettings_PreventCredentialReadMicrophoneAndHttp()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.FailSettingsRead = true;
        Assert.False(fixture.Flow.TryStart());
        Assert.Equal(0, fixture.Credentials.Reads);
        Assert.Equal(0, fixture.Opens);
        Assert.Equal(0, fixture.Requests);
        Assert.False(fixture.Execution.IsRunning);
    }
}

internal sealed class VoiceFlowFixture : IMicrophoneFactory, IAsyncDisposable
{
    public VoiceFlowFixture(bool enabled = true)
    {
        Settings = VrcVaSettings.Default with
        {
            VoiceInput = new() { IsEnabled = enabled, FailedAudioRetentionSeconds = 15 },
        };
        Configuration = new(() => FailSettingsRead ? throw new IOException("private setting content") : Settings,
            settings => { Settings = settings; SavedSettings.Add(JsonSerializer.Serialize(settings)); },
            Credentials, Quotas.Voice,
            (options, credential) => new ObservedTranscriber(this,
                new OpenAiVoiceTranscriber(new(), options, credential, Quotas.Voice, new Handler(this))));
        Flow = new(Execution, this, Configuration.CreateRuntime, Time);
    }
    public ExecutionCoordinator Execution { get; } = new();
    public FeatureUsageQuotas Quotas { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public FakeVoiceCredentials Credentials { get; } = new();
    public VoiceInputConfiguration Configuration { get; }
    public VoiceInputFlow Flow { get; }
    public VrcVaSettings Settings { get; private set; }
    public List<string> SavedSettings { get; } = [];
    public bool FailSettingsRead { get; set; }
    public FakeVoiceMicrophone Microphone { get; private set; } = new();
    public int Opens { get; private set; }
    public int Requests { get; private set; }
    public ReadOnlyMemory<byte> BorrowedAudio { get; private set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } =
        (_, _) => Task.FromResult(Success("synthetic transcript"));

    public void NextMicrophone() => Microphone = new();
    public Task<IMicrophone> OpenAsync(CancellationToken cancellationToken) { Opens++; return Task.FromResult<IMicrophone>(Microphone); }
    public async Task RecordAsync()
    {
        Assert.True(Flow.TryStart());
        await Microphone.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Flow.StopRecording();
        await Flow.WhenIdle.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public ValueTask DisposeAsync() => Flow.DisposeAsync();
    public static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json"),
    };
    private sealed class ObservedTranscriber(VoiceFlowFixture owner, OpenAiVoiceTranscriber inner) : IVoiceTranscriber, IDisposable
    {
        public Task<VoiceTranscription> TranscribeAsync(ReadOnlyMemory<byte> waveBytes, CancellationToken cancellationToken)
        {
            owner.BorrowedAudio = waveBytes;
            return inner.TranscribeAsync(waveBytes, cancellationToken);
        }
        public void Dispose() => inner.Dispose();
    }
    private sealed class Handler(VoiceFlowFixture owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.Requests++;
            Assert.Equal(OpenAiVoiceTranscriberOptions.OfficialEndpoint, request.RequestUri);
            Assert.Equal(owner.Credentials.Value, request.Headers.Authorization!.Parameter);
            return await owner.Response(request, cancellationToken);
        }
    }
}

internal sealed class FakeVoiceCredentials : IVoiceCredentialStore
{
    public string? Value { get; set; } = "fake-voice-key";
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public string? Read() { Reads++; return Value; }
    public void Write(string secret) { Writes++; Value = secret; }
    public void Delete() => Value = null;
}

internal sealed class FakeVoiceMicrophone : IMicrophone
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool DelayCleanup { get; set; }
    public bool FailCleanup { get; set; }
    public bool Disposed { get; private set; }
    public async Task RecordAsync(Action<ReadOnlyMemory<byte>> receiveSamples, CancellationToken stopToken)
    {
        receiveSamples(new byte[] { 0, 64, 0, 192 });
        Started.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stopToken); }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        CleanupStarted.TrySetResult();
        if (DelayCleanup) { await Cleanup.Task; }
        if (FailCleanup) { throw new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed); }
        Disposed = true;
    }
}
