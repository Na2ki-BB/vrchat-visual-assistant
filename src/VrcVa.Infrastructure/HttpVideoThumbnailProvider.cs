using System.Net;
using System.Runtime.InteropServices;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>Dedicated, credential-free thumbnail transport. It never follows redirects or writes files.</summary>
public sealed class HttpVideoThumbnailProvider : IVideoThumbnailProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly IVideoThumbnailDecoder _decoder;
    private readonly TimeSpan _timeout;

    public HttpVideoThumbnailProvider(IVideoThumbnailDecoder decoder)
        : this(decoder, CreateHandler(), VideoThumbnailImage.DownloadTimeout) { }

    internal HttpVideoThumbnailProvider(IVideoThumbnailDecoder decoder, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(handler);
        _decoder = decoder;
        _timeout = timeout ?? VideoThumbnailImage.DownloadTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > VideoThumbnailImage.DownloadTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        PreAuthenticate = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 8,
        MaxConnectionsPerServer = 2,
    };

    public async Task<VideoThumbnailResult> LoadAsync(Uri? thumbnailUrl, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) { return new(VideoThumbnailStatus.Cancelled); }
        if (thumbnailUrl is null) { return new(VideoThumbnailStatus.Missing); }
        if (!VideoMetadata.IsValidThumbnailUrl(thumbnailUrl)) { return new(VideoThumbnailStatus.RejectedUrl); }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, thumbnailUrl);
            using HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) { return new(VideoThumbnailStatus.RedirectRejected); }
            if (!response.IsSuccessStatusCode) { return new(VideoThumbnailStatus.Unavailable); }
            // Defense in depth if an injected/test handler unexpectedly changed the final request URI.
            if (response.RequestMessage?.RequestUri is Uri final && final != thumbnailUrl)
            {
                return new(VideoThumbnailStatus.RedirectRejected);
            }
            if (response.Content.Headers.ContentLength > VideoThumbnailImage.MaximumEncodedBytes)
            {
                return new(VideoThumbnailStatus.TooLarge);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using MemoryStream bytes = new();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + count > VideoThumbnailImage.MaximumEncodedBytes) { return new(VideoThumbnailStatus.TooLarge); }
                bytes.Write(buffer, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                VideoThumbnailImage image = _decoder.Decode(bytes.ToArray());
                timeout.Token.ThrowIfCancellationRequested();
                return new(VideoThumbnailStatus.Available, image);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                or NotSupportedException or FormatException or ExternalException or InvalidOperationException)
            {
                return new(VideoThumbnailStatus.InvalidImage);
            }
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? VideoThumbnailStatus.Cancelled : VideoThumbnailStatus.TimedOut);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return new(VideoThumbnailStatus.Unavailable);
        }
    }

    public void Dispose() => _http.Dispose();
}
