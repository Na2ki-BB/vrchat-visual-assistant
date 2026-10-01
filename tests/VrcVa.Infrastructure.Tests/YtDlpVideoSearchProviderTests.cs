using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class YtDlpVideoSearchProviderTests
{
    [Theory]
    [InlineData("--exec \"danger\" && echo synthetic")]
    [InlineData(" first\r\nsecond\t\"quoted\" 'single' ")]
    [InlineData("https://evil.test/?x=1; $(synthetic) | > file")]
    public async Task Search_PreservesQueryInExactlyOneArgumentAndUsesOnlyApprovedFlags(string query)
    {
        FakeFactory factory = new(new FakeProcess("2026.08.19\n"), new FakeProcess("{\"entries\":[]}"));
        FakeVerifier verifier = new();
        YtDlpVideoSearchProvider provider = Create(factory, verifier);
        VideoSearchRequest request = new(Guid.NewGuid(), Guid.NewGuid(), query);
        Assert.Empty((await provider.SearchAsync(request, default)).Videos);
        Assert.Equal(query, request.Query);
        Assert.Equal(2, factory.Starts.Count);
        ProcessStartInfo info = factory.Starts[1];
        Assert.Equal(YtDlpVideoSearchProvider.ApprovedExecutablePath, info.FileName);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError && info.RedirectStandardInput);
        Assert.Equal("--", info.ArgumentList[^2]);
        Assert.Equal("ytsearch10:" + query, info.ArgumentList[^1]);
        Assert.Single(info.ArgumentList, argument => argument.StartsWith("ytsearch", StringComparison.Ordinal));
        Assert.Equal(new[] { "--ignore-config", "--no-config-locations", "--no-plugin-dirs", "--flat-playlist",
            "--skip-download", "--simulate", "--dump-single-json", "--no-cache-dir", "--no-cookies",
            "--no-cookies-from-browser", "--no-mark-watched", "--no-js-runtimes", "--no-remote-components", "--no-update",
            "--socket-timeout", "10", "--retries", "0", "--extractor-retries", "0", "--encoding", "utf-8", "--", "ytsearch10:" + query }, info.ArgumentList);
        Assert.Equal("--version", factory.Starts[0].ArgumentList[^1]);
        Assert.True(verifier.Lease.Disposed);
        Assert.All(factory.Finished, process => Assert.True(process.Disposed));
    }

    [Fact]
    public void StartInfo_DropsPythonPluginAndProxyEnvironmentWithoutLoggingIt()
    {
        ProcessStartInfo info = YtDlpVideoSearchProvider.CreateStartInfo("self-authored");
        Assert.Equal("1", info.Environment["PYINSTALLER_RESET_ENVIRONMENT"]);
        Assert.All(info.Environment.Keys, key => Assert.Contains(key, new[] {
            "SystemRoot", "WINDIR", "TEMP", "TMP", "PYINSTALLER_RESET_ENVIRONMENT",
        }));
        Assert.DoesNotContain(info.Environment.Keys, key => key.StartsWith("_PYI_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(info.Environment.Keys, key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("YTDLP", StringComparison.OrdinalIgnoreCase) || key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("2026.08.18")]
    [InlineData("private version output")]
    public async Task Search_RejectsVersionWithoutSearching(string version)
    {
        FakeFactory factory = new(new FakeProcess(version));
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Create(factory).SearchAsync(Request(), default));
        Assert.Equal(ScanFailureCode.VideoSearchExecutableRejected, failure.FailureCode);
        Assert.Single(factory.Starts);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(version, failure.ToString());
    }

    [Fact]
    public async Task Search_MissingExecutableAndHashMismatchAreDistinctAndDoNotStart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vrcva-k2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "self-authored-fixture");
            ScanException missing = await Assert.ThrowsAsync<ScanException>(() =>
                ApprovedYtDlpExecutableVerifier.VerifyFileAsync(path, new string('0', 64), default));
            Assert.Equal(ScanFailureCode.VideoSearchNotConfigured, missing.FailureCode);
            await File.WriteAllTextAsync(path, "self-authored binary bytes, never executed");
            ScanException mismatch = await Assert.ThrowsAsync<ScanException>(() =>
                ApprovedYtDlpExecutableVerifier.VerifyFileAsync(path, new string('0', 64), default));
            Assert.Equal(ScanFailureCode.VideoSearchExecutableRejected, mismatch.FailureCode);
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            using IDisposable lease = await ApprovedYtDlpExecutableVerifier.VerifyFileAsync(path, hash, default);
            Assert.DoesNotContain(path, missing.ToString());
            Assert.DoesNotContain(path, mismatch.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Search_CancelledBeforeVerificationDoesNoWork()
    {
        FakeVerifier verifier = new();
        FakeFactory factory = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(factory, verifier).SearchAsync(Request(), new CancellationToken(true)));
        Assert.False(verifier.Called);
        Assert.Empty(factory.Starts);
    }

    [Fact]
    public async Task Runner_StartupFailureNeverIncludesProcessExceptionContents()
    {
        FakeFactory factory = new() { StartFailure = new Win32Exception("private query and stderr") };
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Runner(factory).RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchFailed, failure.FailureCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private", failure.ToString());
    }

    [Fact]
    public async Task Runner_NonzeroExitIsNotNormalZeroResultsAndHidesBothPipes()
    {
        FakeProcess process = new("private stdout", "private stderr", 9);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Runner(new(process)).RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchFailed, failure.FailureCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private", failure.ToString());
        Assert.True(process.Disposed);
        Assert.False(process.Killed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_OneExcessByteKillsTreeAndReapsBeforeDispose(bool stdout)
    {
        int limit = stdout ? YtDlpVideoSearchProvider.MaximumStdoutBytes : YtDlpVideoSearchProvider.MaximumStderrBytes;
        FakeProcess process = new(stdout ? new string('x', limit + 1) : "", stdout ? "" : new string('x', limit + 1));
        process.HoldExit = true;
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Runner(new(process)).RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchOutputTooLarge, failure.FailureCode);
        Assert.True(process.Killed && process.Reaped && process.Disposed);
        Assert.True(process.WaitCount >= 2);
    }

    [Fact]
    public async Task Runner_BothPipesAreReadConcurrentlyAndExactLimitsAreAccepted()
    {
        TaskCompletionSource outputStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource errorStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeProcess process = new("", "")
        {
            Output = new CoordinatedStream(new byte[YtDlpVideoSearchProvider.MaximumStdoutBytes], outputStarted, errorStarted.Task),
            Error = new CoordinatedStream(new byte[YtDlpVideoSearchProvider.MaximumStderrBytes], errorStarted, outputStarted.Task),
        };
        Assert.Equal(YtDlpVideoSearchProvider.MaximumStdoutBytes, (await Runner(new(process)).RunAsync(Start(), default)).StandardOutput.Length);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Runner_TimeoutKillsTreeAndWaitsForReap()
    {
        FakeProcess process = new("", "") { HoldExit = true };
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Runner(new(process), timeout: TimeSpan.FromMilliseconds(30)).RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchTimedOut, failure.FailureCode);
        Assert.True(process.Killed && process.Reaped && process.Disposed);
    }

    [Fact]
    public async Task Runner_UserCancellationDoesNotReturnUntilReaped()
    {
        FakeProcess process = new("", "") { HoldExit = true, HoldReap = true };
        using CancellationTokenSource cancel = new();
        Task search = Runner(new(process)).RunAsync(Start(), cancel.Token);
        cancel.Cancel();
        await process.KillStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(search.IsCompleted);
        Assert.False(process.Disposed);
        process.Reap.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.True(process.Disposed && process.Reaped);
    }

    [Fact]
    public async Task Runner_UnconfirmedCleanupStopsEveryNewAdmissionAndQuarantinesLiveProcess()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        FakeProcess process = new("", "") { HoldExit = true, HoldReap = true };
        FakeFactory factory = new(process);
        YtDlpProcessRunner runner = Runner(factory, execution, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => runner.RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchCleanupFailed, failure.FailureCode);
        Assert.Contains("再起動", failure.UserMessage);
        Assert.False(process.Disposed);
        operation!.Dispose();
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        await Assert.ThrowsAsync<ScanException>(() => runner.RunAsync(Start(), default));
        Assert.Single(factory.Starts);
        process.Reap.TrySetResult();
    }

    [Fact]
    public async Task Runner_CancellationInsensitivePipeStillHasBoundedCleanupAndStopsAdmissions()
    {
        ExecutionCoordinator execution = new();
        NonCancellingStream pipe = new();
        FakeProcess process = new("", "") { Output = pipe, HoldExit = true };
        YtDlpProcessRunner runner = Runner(new(process), execution,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => runner.RunAsync(Start(), default));
        Assert.Equal(ScanFailureCode.VideoSearchCleanupFailed, failure.FailureCode);
        Assert.True(process.Killed && process.Reaped);
        Assert.False(process.Disposed);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        pipe.Finish.TrySetResult(0);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("unpaired-surrogate")]
    public async Task Search_RejectsUnrepresentableArgumentWithoutModifyingOrSendingQuery(string query)
    {
        if (query == "unpaired-surrogate") { query = "a" + '\ud800' + "b"; }
        FakeFactory factory = new();
        FakeVerifier verifier = new();
        VideoSearchRequest request = new(Guid.NewGuid(), Guid.NewGuid(), query);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => Create(factory, verifier).SearchAsync(request, default));
        Assert.Equal(ScanFailureCode.VideoSearchFailed, failure.FailureCode);
        Assert.Equal(query, request.Query);
        Assert.Empty(factory.Starts);
        Assert.False(verifier.Called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_TerminalCleanupFailureKeepsRestartReasonWhileRejectingStalePresentation(bool cancel)
    {
        ExecutionCoordinator execution = new();
        FakeProcess process = new("private synthetic stdout", "private synthetic stderr") { HoldExit = true, HoldReap = true };
        FakeFactory factory = new(new FakeProcess("2026.08.19"), process);
        YtDlpVideoSearchProvider provider = new(new FakeVerifier(), factory, execution,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        VideoSearchSession session = new(execution);
        RecordingRenderer renderer = new();
        string folder = Path.Combine(Path.GetTempPath(), "vrcva-k2-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScanPipeline pipeline = new(new NoCapture(), new FeatureCatalog(new FeatureEntry(
                BuiltInFeatures.DirectVideoSearch, new DirectVideoSearchHandler(provider, session))),
                renderer, new PrivacySafeFileLogger(folder), execution);
            Task<ScanOutcome> pending = pipeline.RunAsync(ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch,
                TextInputSession.Create("private synthetic query")));
            if (cancel) { execution.CancelCurrentOperation(); }
            ScanOutcome outcome = await pending;
            Assert.Equal(ScanFailureCode.VideoSearchCleanupFailed, outcome.Failure!.Code);
            Assert.Contains("再起動", outcome.Failure.Message);
            Assert.Null(session.CurrentResult);
            Assert.Empty(renderer.Outcomes);
            Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
            Assert.False(process.Disposed);
            string logs = string.Join("", Directory.GetFiles(folder).Select(File.ReadAllText));
            Assert.DoesNotContain("private synthetic", logs);
            process.Reap.TrySetResult();
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    }

    private sealed class NoCapture : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Capture is outside video search.");
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanOutcome> Outcomes { get; } = [];
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken token) => Task.CompletedTask;
        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken token)
        { Outcomes.Add(outcome); return Task.CompletedTask; }
    }

    private static VideoSearchRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), "Self-authored private query");
    private static ProcessStartInfo Start() => YtDlpVideoSearchProvider.CreateStartInfo("self-authored");
    private static YtDlpVideoSearchProvider Create(FakeFactory factory, FakeVerifier? verifier = null) =>
        new(verifier ?? new(), factory, new ExecutionCoordinator(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    private static YtDlpProcessRunner Runner(FakeFactory factory, ExecutionCoordinator? execution = null,
        TimeSpan? timeout = null, TimeSpan? cleanup = null) => new(factory, (execution ?? new()).Stop,
            timeout ?? TimeSpan.FromSeconds(2), cleanup ?? TimeSpan.FromSeconds(2));

    private sealed class FakeVerifier : IYtDlpExecutableVerifier
    {
        public bool Called { get; private set; }
        public FakeLease Lease { get; } = new();
        public Task<IDisposable> VerifyAsync(CancellationToken token) { Called = true; return Task.FromResult<IDisposable>(Lease); }
    }
    private sealed class FakeLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeFactory(params FakeProcess[] processes) : IYtDlpProcessFactory
    {
        private readonly Queue<FakeProcess> _processes = new(processes);
        public List<ProcessStartInfo> Starts { get; } = [];
        public List<FakeProcess> Finished { get; } = [];
        public Exception? StartFailure { get; init; }
        public IYtDlpProcess Start(ProcessStartInfo info)
        {
            Starts.Add(info);
            if (StartFailure is not null) { throw StartFailure; }
            FakeProcess process = _processes.Dequeue();
            Finished.Add(process);
            return process;
        }
    }
    private sealed class FakeProcess(string output, string error = "", int exitCode = 0) : IYtDlpProcess
    {
        public Stream Output { get; init; } = new MemoryStream(Encoding.UTF8.GetBytes(output));
        public Stream Error { get; init; } = new MemoryStream(Encoding.UTF8.GetBytes(error));
        public Stream StandardOutput => Output;
        public Stream StandardError => Error;
        public int ExitCode => exitCode;
        public bool HoldExit { get; set; }
        public bool HoldReap { get; init; }
        public bool Killed { get; private set; }
        public bool Reaped { get; private set; }
        public bool Disposed { get; private set; }
        public int WaitCount { get; private set; }
        public TaskCompletionSource KillStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitForExitAsync(CancellationToken token)
        {
            WaitCount++;
            if (HoldExit && !Killed) { await KillStarted.Task.WaitAsync(token); }
            if (HoldReap) { await Reap.Task.WaitAsync(token); }
            Reaped = true;
        }
        public void KillTree() { Killed = true; KillStarted.TrySetResult(); }
        public void Dispose() { Assert.True(Reaped); Disposed = true; Output.Dispose(); Error.Dispose(); }
    }
    private sealed class NonCancellingStream : MemoryStream
    {
        public TaskCompletionSource<int> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(Finish.Task);
    }
    private sealed class CoordinatedStream(byte[] bytes, TaskCompletionSource started, Task otherStarted) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            started.TrySetResult();
            await otherStarted.WaitAsync(token);
            return await base.ReadAsync(buffer, token);
        }
    }
}
