using System.Security.Cryptography;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

internal interface IYtDlpExecutableVerifier
{
    Task<IDisposable> VerifyAsync(CancellationToken cancellationToken);
}

internal sealed class ApprovedYtDlpExecutableVerifier : IYtDlpExecutableVerifier
{
    public Task<IDisposable> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchNotConfigured,
                "動画検索は承認済みyt-dlpを配置したWindows x64環境で利用できます。");
        }
        return VerifyFileAsync(YtDlpVideoSearchProvider.ApprovedExecutablePath,
            YtDlpVideoSearchProvider.ApprovedSha256, cancellationToken);
    }

    internal static async Task<IDisposable> VerifyFileAsync(string path, string expectedHash, CancellationToken token)
    {
        FileStream? file = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // Reject redirected files/directories rather than following a link outside the approved location.
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchExecutableRejected,
                        "yt-dlpの配置にリンクが含まれています。公式固定版を通常のフォルダーへ配置してください。");
                }
            }
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchExecutableRejected,
                    "yt-dlpのSHA256が承認済みの固定版と一致しません。公式版の配置を確認してください。");
            }
            IDisposable lease = file;
            file = null;
            return lease;
        }
        catch (FileNotFoundException) { throw Missing(); }
        catch (DirectoryNotFoundException) { throw Missing(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw YtDlpVideoSearchProvider.Failure(ScanFailureCode.VideoSearchExecutableRejected,
                "yt-dlpの実行ファイルを検証できません。公式固定版の配置とアクセスを確認してください。");
        }
        finally { file?.Dispose(); }
    }

    private static ScanException Missing() => YtDlpVideoSearchProvider.Failure(
        ScanFailureCode.VideoSearchNotConfigured, "yt-dlpが未導入です。承認済みの公式固定版を指定の場所へ配置してください。");
}
