using System.Net;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class HttpVideoThumbnailProviderTests
{
    private static readonly Uri Allowed = new("https://i.ytimg.com/vi/sample00000/default.jpg");

    [Theory]
    [InlineData("http://i.ytimg.com/a")]
    [InlineData("https://localhost/a")]
    [InlineData("https://127.0.0.1/a")]
    [InlineData("https://[::1]/a")]
    [InlineData("https://192.168.1.1/a")]
    [InlineData("file:///tmp/image.png")]
    [InlineData("https://i.ytimg.com.example.test/a")]
    [InlineData("https://i.ytimg.com:444/a")]
    [InlineData("https://user:pass@i.ytimg.com/a")]
    [InlineData("https://i.ytimg.com/a#fragment")]
    public async Task ForbiddenUrl_IsRejectedBeforeHttp(string url)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        FakeDecoder decoder = new();
        using HttpVideoThumbnailProvider provider = new(decoder, handler);
        VideoThumbnailResult result = await provider.LoadAsync(new Uri(url), CancellationToken.None);
        Assert.Equal(VideoThumbnailStatus.RejectedUrl, result.Status);
        Assert.True(result.UsePlaceholder);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, decoder.Calls);
    }

    [Fact]
    public async Task MissingThumbnail_AndCancellationHaveNoNetworkSideEffects()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpVideoThumbnailProvider provider = new(new FakeDecoder(), handler);
        Assert.Equal(VideoThumbnailStatus.Missing, (await provider.LoadAsync(null, CancellationToken.None)).Status);
        Assert.Equal(VideoThumbnailStatus.Cancelled, (await provider.LoadAsync(Allowed, new CancellationToken(true))).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://localhost/a")]
    [InlineData("https://i.ytimg.com/another.jpg")]
    [InlineData("https://img.youtube.com/another.jpg")]
    public async Task EveryRedirect_IsRejectedWithoutFollowingOrDecoding(string destination)
    {
        RecordingHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Found);
            response.Headers.Location = new Uri(destination);
            return response;
        });
        FakeDecoder decoder = new();
        using HttpVideoThumbnailProvider provider = new(decoder, handler);
        Assert.Equal(VideoThumbnailStatus.RedirectRejected, (await provider.LoadAsync(Allowed, CancellationToken.None)).Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, decoder.Calls);
    }

    [Fact]
    public async Task ChangedFinalRequestUri_IsRejectedAsAnUnexpectedRedirect()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://localhost/a"),
            Content = new ByteArrayContent([1]),
        });
        using HttpVideoThumbnailProvider provider = new(new FakeDecoder(), handler);
        Assert.Equal(VideoThumbnailStatus.RedirectRejected, (await provider.LoadAsync(Allowed, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBody_IsRejectedWithAndWithoutAContentLength(bool declared)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declared ? new ByteArrayContent(new byte[VideoThumbnailImage.MaximumEncodedBytes + 1])
                : new StreamContent(new NonSeekStream(new byte[VideoThumbnailImage.MaximumEncodedBytes + 1])),
        });
        FakeDecoder decoder = new();
        using HttpVideoThumbnailProvider provider = new(decoder, handler);
        Assert.Equal(VideoThumbnailStatus.TooLarge, (await provider.LoadAsync(Allowed, CancellationToken.None)).Status);
        Assert.Equal(0, decoder.Calls);
    }

    [Fact]
    public async Task ExactByteLimit_ReachesDecoderAndSendsNoCredentialsOrCookies()
    {
        RecordingHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Allowed, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            Assert.Null(request.Content);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[VideoThumbnailImage.MaximumEncodedBytes]) };
        });
        FakeDecoder decoder = new();
        using HttpVideoThumbnailProvider provider = new(decoder, handler);
        VideoThumbnailResult result = await provider.LoadAsync(Allowed, CancellationToken.None);
        Assert.Equal(VideoThumbnailStatus.Available, result.Status);
        Assert.False(result.UsePlaceholder);
        Assert.Equal(VideoThumbnailImage.MaximumEncodedBytes, decoder.Bytes);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void ProductionHandler_DisablesCredentialsCookiesRedirectsProxyAndDecompression()
    {
        using HttpClientHandler handler = HttpVideoThumbnailProvider.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task InvalidImageOrHttpFailure_ReturnsPlaceholderWithOneAttempt()
    {
        FakeDecoder decoder = new() { Invalid = true };
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using HttpVideoThumbnailProvider provider = new(decoder, handler);
        VideoThumbnailResult bad = await provider.LoadAsync(Allowed, CancellationToken.None);
        Assert.Equal(VideoThumbnailStatus.InvalidImage, bad.Status);
        Assert.True(bad.UsePlaceholder);
        Assert.Null(bad.Image);
        Assert.Equal(1, handler.Calls);
        using HttpVideoThumbnailProvider failed = new(new FakeDecoder(), new RecordingHandler(_ => throw new HttpRequestException("synthetic URL-bearing exception")));
        Assert.Equal(VideoThumbnailStatus.Unavailable, (await failed.LoadAsync(Allowed, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellation_AreDistinctAndDoNotRetry()
    {
        SlowHandler handler = new();
        using HttpVideoThumbnailProvider provider = new(new FakeDecoder(), handler, TimeSpan.FromMilliseconds(20));
        Assert.Equal(VideoThumbnailStatus.TimedOut, (await provider.LoadAsync(Allowed, CancellationToken.None)).Status);
        using CancellationTokenSource cancellation = new();
        Task<VideoThumbnailResult> pending = provider.LoadAsync(Allowed, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(VideoThumbnailStatus.Cancelled, (await pending).Status);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class FakeDecoder : IVideoThumbnailDecoder
    {
        public int Calls { get; private set; }
        public int Bytes { get; private set; }
        public bool Invalid { get; init; }
        public VideoThumbnailImage Decode(ReadOnlyMemory<byte> encodedImage)
        {
            Calls++;
            Bytes = encodedImage.Length;
            if (Invalid) { throw new InvalidDataException("synthetic private image detail"); }
            return new(1, 1, [0, 0, 0, 255]);
        }
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
    private sealed class NonSeekStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
