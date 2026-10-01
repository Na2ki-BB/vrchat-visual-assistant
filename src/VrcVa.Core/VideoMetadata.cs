using System.Text;

namespace VrcVa.Core;

/// <summary>
/// Validated, in-memory video metadata. Provider URLs never survive canonicalization.
/// Titles are literal display text; thumbnail retrieval is a separate boundary.
/// </summary>
public sealed class VideoMetadata
{
    public const int MaximumTitleUtf8Bytes = 4_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public VideoMetadata(string videoId, string title, string? providerUrl = null, Uri? thumbnailUrl = null)
    {
        ArgumentNullException.ThrowIfNull(videoId);
        if (videoId.Length != 11 || videoId.Any(character => !IsVideoIdCharacter(character)))
        {
            throw new ArgumentException("The video ID is invalid.", nameof(videoId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            throw new ArgumentException("The title is invalid.", nameof(title));
        }

        try
        {
            if (StrictUtf8.GetByteCount(title) > MaximumTitleUtf8Bytes)
            {
                throw new ArgumentException("The title exceeds the input limit.", nameof(title));
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The title is invalid.", nameof(title));
        }

        if (providerUrl is not null && !IsValidProviderUrl(providerUrl, videoId))
        {
            throw new ArgumentException("The provider URL is invalid.", nameof(providerUrl));
        }

        if (thumbnailUrl is not null && !IsValidThumbnailUrl(thumbnailUrl))
        {
            throw new ArgumentException("The thumbnail URL is invalid.", nameof(thumbnailUrl));
        }

        VideoId = videoId;
        Title = title;
        WatchUrl = new Uri("https://www.youtube.com/watch?v=" + videoId, UriKind.Absolute);
        ThumbnailUrl = thumbnailUrl;
    }

    public string VideoId { get; }

    public string Title { get; }

    public Uri WatchUrl { get; }

    public Uri? ThumbnailUrl { get; }

    private static bool IsVideoIdCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';

    private static bool IsValidProviderUrl(string original, string videoId)
    {
        if (!TryReadHttpsUrl(original, out Uri? uri, out string authority, out string path, out string query)
            || !(IsAuthority(authority, "www.youtube.com")
                || IsAuthority(authority, "youtube.com")
                || IsAuthority(authority, "youtu.be")))
        {
            return false;
        }

        bool shortUrl = string.Equals(uri!.Host, "youtu.be", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(path, shortUrl ? "/" + videoId : "/watch", StringComparison.Ordinal))
        {
            return false;
        }

        int videoParameters = 0;
        foreach (string parameter in query.Split('&'))
        {
            int equals = parameter.IndexOf('=');
            string key = equals < 0 ? parameter : parameter[..equals];
            // Encoded aliases must not conceal a second v parameter.
            if (!string.Equals(Uri.UnescapeDataString(key), "v", StringComparison.Ordinal))
            {
                continue;
            }

            videoParameters++;
            if (videoParameters > 1
                || !string.Equals(key, "v", StringComparison.Ordinal)
                || equals < 0
                || !string.Equals(parameter[(equals + 1)..], videoId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return shortUrl || videoParameters == 1;
    }

    public static bool IsValidThumbnailUrl(Uri thumbnailUrl) =>
        thumbnailUrl.IsAbsoluteUri
        && TryReadHttpsUrl(thumbnailUrl.OriginalString, out _, out string authority, out _, out _)
        && (IsAuthority(authority, "i.ytimg.com") || IsAuthority(authority, "img.youtube.com"));

    private static bool TryReadHttpsUrl(string original, out Uri? uri, out string authority, out string path, out string query)
    {
        uri = null;
        authority = string.Empty;
        path = string.Empty;
        query = string.Empty;
        if (!original.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || original.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '#')
            || !Uri.TryCreate(original, UriKind.Absolute, out uri)
            || !uri.IsWellFormedOriginalString()
            || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort
            || uri.Fragment.Length != 0)
        {
            return false;
        }

        // Read the raw authority/path so Uri cannot normalize a forbidden spelling
        // (escaped host/path, dot segments, backslashes) into an allowed watch URL.
        int authorityEnd = original.IndexOfAny(['/', '?'], "https://".Length);
        if (authorityEnd < 0)
        {
            authorityEnd = original.Length;
        }

        authority = original["https://".Length..authorityEnd];
        int queryStart = original.IndexOf('?', authorityEnd);
        int pathEnd = queryStart < 0 ? original.Length : queryStart;
        path = original[authorityEnd..pathEnd];
        query = queryStart < 0 ? string.Empty : original[(queryStart + 1)..];
        return true;
    }

    private static bool IsAuthority(string authority, string host) =>
        string.Equals(authority, host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(authority, host + ":443", StringComparison.OrdinalIgnoreCase);
}
