using System.ComponentModel;
using System.Diagnostics;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

internal interface IYtDlpProcessFactory
{
    IYtDlpProcess Start(ProcessStartInfo startInfo);
}

internal interface IYtDlpProcess : IDisposable
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillTree();
}

internal sealed class SystemYtDlpProcessFactory : IYtDlpProcessFactory
{
    public IYtDlpProcess Start(ProcessStartInfo startInfo)
    {
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        process.StandardInput.Close();
        return new SystemYtDlpProcess(process);
    }

    private sealed class SystemYtDlpProcess(Process process) : IYtDlpProcess
    {
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void KillTree()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }
}

/// <summary>Concurrent bounded pipes, tree kill, and independently bounded reap. Never emits process contents.</summary>
internal sealed class YtDlpProcessRunner(
    IYtDlpProcessFactory processes,
    Action stopAdmissions,
    TimeSpan timeout,
    TimeSpan cleanupTimeout)
{
    // An unreaped process and its pipes stay owned until the app exits. No new operation may start.
    private IYtDlpProcess? _quarantinedProcess;
    private Task? _quarantinedCleanup;

    internal async Task<YtDlpProcessOutput> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_quarantinedProcess is not null)
        {
            throw CleanupFailure();
        }
        IYtDlpProcess process;
        try { process = processes.Start(info); }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchFailed,
                "yt-dlpを起動できませんでした。公式固定版の配置を確認してください。");
        }

        using CancellationTokenSource timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(timeout);
        // Read errors/overflow cancel the sibling read and the exit wait promptly, rather than waiting for Task.WhenAll.
        TaskCompletionSource<Exception> readFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput,
            YtDlpVideoSearchProvider.MaximumStdoutBytes, timed, readFailure);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError,
            YtDlpVideoSearchProvider.MaximumStderrBytes, timed, readFailure);
        Task exit = process.WaitForExitAsync(timed.Token);
        Task primaryWork = Task.WhenAll(stdout, stderr, exit);
        bool cleanupConfirmed = false;
        try
        {
            await primaryWork.WaitAsync(timed.Token).ConfigureAwait(false);
            cleanupConfirmed = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchFailed,
                    "yt-dlpによる動画検索に失敗しました。やり直してください。");
            }
            return new YtDlpProcessOutput(await stdout.ConfigureAwait(false), (await stderr.ConfigureAwait(false)).Length != 0);
        }
        catch (Exception exception) when (exception is not ScanException || !cleanupConfirmed)
        {
            // ExitCode is not a reaping signal. On every interrupted path kill the tree and await a fresh exit wait.
            Task? cleanupWork = null;
            try
            {
                process.KillTree();
                using CancellationTokenSource cleanupDeadline = new(cleanupTimeout);
                cleanupWork = Task.WhenAll(process.WaitForExitAsync(cleanupDeadline.Token),
                    ObserveAsync(primaryWork));
                await cleanupWork.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                cleanupConfirmed = true;
            }
            catch (Exception)
            {
                _quarantinedProcess = process;
                // Observe any late faults; keep the actual process/streams alive, without claiming successful cleanup.
                _quarantinedCleanup = ObserveAsync(Task.WhenAll(cleanupWork ?? Task.CompletedTask, primaryWork));
                stopAdmissions();
                throw CleanupFailure();
            }
            if (readFailure.Task.IsCompletedSuccessfully && readFailure.Task.Result is ScanException outputFailure) { throw outputFailure; }
            if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            if (!readFailure.Task.IsCompletedSuccessfully && (exception is OperationCanceledException || timed.IsCancellationRequested))
            {
                throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchTimedOut,
                    "動画検索が時間内に完了しませんでした。やり直してください。");
            }
            throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchFailed,
                "yt-dlpの応答を取得できませんでした。やり直してください。");
        }
        finally
        {
            if (cleanupConfirmed) { process.Dispose(); }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationTokenSource timed, TaskCompletionSource<Exception> readFailure)
    {
        try
        {
            using MemoryStream output = new();
            byte[] buffer = new byte[8_192];
            while (true)
            {
                // Read at most one excess byte, so memory use never grows beyond the fixed cap.
                int count = await stream.ReadAsync(buffer.AsMemory(0,
                    Math.Min(buffer.Length, maximumBytes - (int)output.Length + 1)), timed.Token).ConfigureAwait(false);
                if (count == 0) { return output.ToArray(); }
                if (output.Length + count > maximumBytes)
                {
                    throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchOutputTooLarge,
                        "動画検索の応答がサイズ上限を超えました。やり直してください。");
                }
                output.Write(buffer, 0, count);
            }
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException) { readFailure.TrySetResult(exception); }
            timed.Cancel();
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static ScanException CleanupFailure() => YtDlpVideoSearchProvider.Failure(
        ScanFailureCode.VideoSearchCleanupFailed,
        "yt-dlpの終了を確認できませんでした。新しい処理を停止しました。アプリを終了し、再起動してください。");
}

internal sealed class YtDlpProcessOutput(byte[] standardOutput, bool hasStandardError)
{
    public byte[] StandardOutput { get; } = standardOutput;
    public bool HasStandardError { get; } = hasStandardError;
}
