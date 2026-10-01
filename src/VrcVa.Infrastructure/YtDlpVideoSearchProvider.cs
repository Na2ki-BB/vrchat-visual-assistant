using System.Diagnostics;
using System.Text;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>One metadata-only search, using the approved binary and no caller-supplied options.</summary>
public sealed class YtDlpVideoSearchProvider : IVideoSearchProvider
{
    public const string ApprovedVersion = "2026.08.19";
    public const string ApprovedSha256 = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";
    public const int MaximumStdoutBytes = 1_048_576;
    public const int MaximumStderrBytes = 65_536;
    public static TimeSpan SearchTimeout => TimeSpan.FromSeconds(30);
    public static TimeSpan CleanupTimeout => TimeSpan.FromSeconds(5);

    private static readonly string[] FixedArguments =
    [
        "--ignore-config", "--no-config-locations", "--no-plugin-dirs", "--flat-playlist",
        "--skip-download", "--simulate", "--dump-single-json", "--no-cache-dir",
        "--no-cookies", "--no-cookies-from-browser", "--no-mark-watched",
        "--no-js-runtimes", "--no-remote-components", "--no-update",
        "--socket-timeout", "10", "--retries", "0", "--extractor-retries", "0",
        "--encoding", "utf-8",
    ];

    private readonly IYtDlpExecutableVerifier _verifier;
    private readonly YtDlpProcessRunner _runner;
    private int _running;
    private IDisposable? _quarantinedExecutable;

    public YtDlpVideoSearchProvider(ExecutionCoordinator execution)
        : this(new ApprovedYtDlpExecutableVerifier(), new SystemYtDlpProcessFactory(), execution,
            SearchTimeout, CleanupTimeout)
    {
    }

    internal YtDlpVideoSearchProvider(
        IYtDlpExecutableVerifier verifier,
        IYtDlpProcessFactory processes,
        ExecutionCoordinator execution,
        TimeSpan timeout,
        TimeSpan cleanupTimeout)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(execution);
        _verifier = verifier;
        _runner = new YtDlpProcessRunner(processes, execution.Stop, timeout, cleanupTimeout);
    }

    public static string ApprovedExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcVa", "tools", "yt-dlp", ApprovedVersion, "yt-dlp.exe");

    public async Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_quarantinedExecutable is not null)
        {
            throw Failure(ScanFailureCode.VideoSearchCleanupFailed, "yt-dlpの終了を確認できません。アプリを再起動してください。");
        }
        if (request.Query.Contains('\0') || !IsValidUnicode(request.Query))
        {
            throw Failure(ScanFailureCode.VideoSearchFailed, "検索語に送信できない文字が含まれています。認識文を確認してください。");
        }
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw Failure(ScanFailureCode.Busy, "動画検索は処理中です。完了までお待ちください。");
        }
        using CancellationTokenSource deadline = new(SearchTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        IDisposable? verified = null;
        try
        {
            // Keep the verified file handle open, denying writes/replacement on Windows until the process drains.
            verified = await _verifier.VerifyAsync(linked.Token).ConfigureAwait(false);
            YtDlpProcessOutput version = await _runner.RunAsync(CreateStartInfo(versionOnly: true), linked.Token).ConfigureAwait(false);
            if (!Encoding.UTF8.GetString(version.StandardOutput).Trim().Equals(ApprovedVersion, StringComparison.Ordinal))
            {
                throw Failure(ScanFailureCode.VideoSearchExecutableRejected, "yt-dlpの版が承認済みの固定版と一致しません。公式版の配置を確認してください。");
            }

            YtDlpProcessOutput json = await _runner.RunAsync(CreateStartInfo(query: request.Query), linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return YtDlpMetadataParser.Parse(json.StandardOutput, json.HasStandardError);
        }
        catch (ScanException exception) when (exception.FailureCode == ScanFailureCode.VideoSearchCleanupFailed)
        {
            _quarantinedExecutable = verified;
            verified = null;
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(ScanFailureCode.VideoSearchTimedOut, "動画検索が時間内に完了しませんでした。やり直してください。");
        }
        finally
        {
            verified?.Dispose();
            Volatile.Write(ref _running, 0);
        }
    }

    private static bool IsValidUnicode(string query)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(query); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static ProcessStartInfo CreateStartInfo(string? query = null, bool versionOnly = false)
    {
        ProcessStartInfo info = new(ApprovedExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(ApprovedExecutablePath)!,
        };
        foreach (string argument in FixedArguments) { info.ArgumentList.Add(argument); }
        if (versionOnly) { info.ArgumentList.Add("--version"); }
        else
        {
            ArgumentNullException.ThrowIfNull(query);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add("ytsearch10:" + query);
        }

        // Do not transmit application secrets, proxy credentials, Python/plugin or bootloader overrides.
        // The approved standalone Windows binary only needs OS and temporary-directory locations.
        info.Environment.Clear();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            info.Environment["SystemRoot"] = windows;
            info.Environment["WINDIR"] = windows;
        }
        info.Environment["TEMP"] = Path.GetTempPath();
        info.Environment["TMP"] = Path.GetTempPath();
        info.Environment["PYINSTALLER_RESET_ENVIRONMENT"] = "1";
        return info;
    }

    internal static ScanException Failure(ScanFailureCode code, string message) =>
        new(code, ScanStage.TextHandling, message);
}
