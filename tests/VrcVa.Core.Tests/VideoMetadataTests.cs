using System.Text;

namespace VrcVa.Core.Tests;

public sealed class VideoMetadataTests
{
    private const string VideoId = "Abc_123-xyz";
    private const string CanonicalUrl = "https://www.youtube.com/watch?v=" + VideoId;

    [Theory]
    [InlineData("Abc_123-xyz")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("abcdefghijk")]
    [InlineData("0123456789_")]
    [InlineData("_________--")]
    public void VideoId_AcceptsOnlyElevenAsciiIdCharacters(string videoId)
    {
        VideoMetadata metadata = new(videoId, "Title");

        Assert.Equal(videoId, metadata.VideoId);
        Assert.Equal("https://www.youtube.com/watch?v=" + videoId, metadata.WatchUrl.AbsoluteUri);
        Assert.Null(metadata.ThumbnailUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Abc_123-xy")]
    [InlineData("Abc_123-xyz0")]
    [InlineData("Abc_123-xy!")]
    [InlineData("Abc_123-xy ")]
    [InlineData("Abc_123-xy\n")]
    [InlineData("Abc_123-xyé")]
    [InlineData("Ａbc_123-xyz")]
    public void VideoId_RejectsInvalidValues(string videoId)
    {
        Assert.Throws<ArgumentException>(() => new VideoMetadata(videoId, "Title"));
    }

    [Fact]
    public void RequiredFields_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => new VideoMetadata(null!, "Title"));
        Assert.Throws<ArgumentNullException>(() => new VideoMetadata(VideoId, null!));
    }

    [Fact]
    public void Title_IsPreservedAsLiteralText()
    {
        const string title = "  日本語 <b>literal</b> & \"quotes\"  ";
        VideoMetadata metadata = new(VideoId, title);

        Assert.Equal(title, metadata.Title);
    }

    [Fact]
    public void Title_EnforcesExactUtf8ByteLimitWithoutTruncation()
    {
        string ascii = new('a', VideoMetadata.MaximumTitleUtf8Bytes);
        string japanese = new string('あ', 1_333) + "a";
        string supplementary = string.Concat(Enumerable.Repeat("🎵", 1_000));

        Assert.Equal(4_000, VideoMetadata.MaximumTitleUtf8Bytes);
        foreach (string title in new[] { ascii, japanese, supplementary })
        {
            Assert.Equal(4_000, Encoding.UTF8.GetByteCount(title));
            Assert.Equal(title, new VideoMetadata(VideoId, title).Title);
            Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, title + "a"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u3000")]
    [InlineData("Title\nNext")]
    [InlineData("Title\rNext")]
    [InlineData("Title\tNext")]
    [InlineData("Title\0Next")]
    [InlineData("Title\u007fNext")]
    [InlineData("Title\u0085Next")]
    [InlineData("Title\u2028Next")]
    [InlineData("Title\u2029Next")]
    public void Title_RejectsEmptyOrMultilineControlText(string title)
    {
        Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, title));
    }

    [Fact]
    public void Title_RejectsInvalidUnicodeWithoutLeakingEncoderDetails()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, "PrivateTitle\ud800"));

        Assert.DoesNotContain("PrivateTitle", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://youtu.be/Abc_123-xyz")]
    [InlineData("https://www.youtube.com:443/watch?v=Abc_123-xyz")]
    [InlineData("https://youtu.be:443/Abc_123-xyz")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/watch?v=Abc_123-xyz")]
    [InlineData("https://youtube.com/watch?list=private-list&v=Abc_123-xyz&t=120&si=private-token")]
    [InlineData("https://youtu.be/Abc_123-xyz?t=120&si=private-token")]
    [InlineData("https://youtu.be/Abc_123-xyz?v=Abc_123-xyz")]
    public void ProviderUrl_CanonicalizesAllowedFormsAndDiscardsUnrelatedQuery(string providerUrl)
    {
        VideoMetadata metadata = new(VideoId, "Title", providerUrl);

        Assert.Equal(CanonicalUrl, metadata.WatchUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Abc_123-xyz")]
    [InlineData("/watch?v=Abc_123-xyz")]
    [InlineData("http://www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("ftp://www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=Abc_123-xyz")]
    [InlineData("https://evil.example/watch?v=Abc_123-xyz")]
    [InlineData("https://m.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com./watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com:444/watch?v=Abc_123-xyz")]
    [InlineData("https://user@www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://user:password@www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://@www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz#fragment")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz#")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?V=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=Other123456")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz&v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz&v=Other123456")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz&%76=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?%76=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=%41bc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz%26v=Other123456")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz;")]
    [InlineData("https://www.youtube.com/watch/extra?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch/?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/Watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/shorts/Abc_123-xyz")]
    [InlineData("https://www.youtube.com/embed/Abc_123-xyz")]
    [InlineData("https://youtu.be/Other123456")]
    [InlineData("https://youtu.be/Abc_123-xyz/")]
    [InlineData("https://youtu.be/Abc_123-xyz/extra")]
    [InlineData("https://youtu.be/Abc_123-xyz?v=Other123456")]
    [InlineData("https://youtu.be/Abc_123-xyz?v=Abc_123-xyz&v=Abc_123-xyz")]
    public void ProviderUrl_RejectsUnapprovedOriginsPathsAndAmbiguousIds(string providerUrl)
    {
        Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, "Title", providerUrl));
    }

    [Theory]
    [InlineData(" https://www.youtube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz ")]
    [InlineData("https://www.youtube.com/watch?v=Abc_123-xyz\r\n")]
    [InlineData("https://www.youtube.com/\twatch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com\\watch?v=Abc_123-xyz")]
    [InlineData("https:\\www.youtube.com\\watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/%77atch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/a/../watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/./watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/%2e/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/a/%2e%2e/watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com//watch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/%2fwatch?v=Abc_123-xyz")]
    [InlineData("https://www.youtube.com/%5cwatch?v=Abc_123-xyz")]
    [InlineData("https://www.%79outube.com/watch?v=Abc_123-xyz")]
    [InlineData("https://youtu.be/%41bc_123-xyz")]
    [InlineData("https://youtu.be/a/../Abc_123-xyz")]
    [InlineData("https:////www.youtube.com/watch?v=Abc_123-xyz")]
    public void ProviderUrl_RejectsParserNormalizationBypasses(string providerUrl)
    {
        Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, "Title", providerUrl));
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://img.youtube.com/vi/Abc_123-xyz/0.jpg")]
    [InlineData("https://i.ytimg.com:443/vi/Abc_123-xyz/default.jpg?sqp=sample")]
    [InlineData("HTTPS://I.YTIMG.COM/vi/Abc_123-xyz/default.jpg")]
    public void ThumbnailUrl_AcceptsOnlyAllowedHttpsOrigins(string thumbnailUrl)
    {
        Uri thumbnail = new(thumbnailUrl, UriKind.Absolute);
        VideoMetadata metadata = new(VideoId, "Title", thumbnailUrl: thumbnail);

        Assert.Same(thumbnail, metadata.ThumbnailUrl);
    }

    [Theory]
    [InlineData("/vi/Abc_123-xyz/default.jpg")]
    [InlineData("http://i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://i.ytimg.com:444/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://i.ytimg.com:80/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://user@i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://@i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://i.ytimg.com/vi/Abc_123-xyz/default.jpg#fragment")]
    [InlineData("https://i.ytimg.com/vi/Abc_123-xyz/default.jpg#")]
    [InlineData("https://i.ytimg.com.evil.example/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://sub.i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://i.ytimg.com./vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://www.youtube.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://localhost/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://127.0.0.1/vi/Abc_123-xyz/default.jpg")]
    [InlineData("file:///vi/Abc_123-xyz/default.jpg")]
    [InlineData(" https://i.ytimg.com/vi/Abc_123-xyz/default.jpg")]
    [InlineData("https://i.ytimg.com/vi\\Abc_123-xyz/default.jpg")]
    public void ThumbnailUrl_RejectsUnapprovedOrNormalizedOrigins(string thumbnailUrl)
    {
        Uri thumbnail = new(thumbnailUrl, UriKind.RelativeOrAbsolute);

        Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, "Title", thumbnailUrl: thumbnail));
    }

    [Fact]
    public void Diagnostics_DoNotContainMetadataOrRejectedContent()
    {
        const string privateTitle = "Private candidate title";
        const string invalidId = "Private invalid ID";
        const string invalidProvider = "https://private.example/watch?secret=value";
        Uri invalidThumbnail = new("https://private.example/private-image.jpg");
        VideoMetadata metadata = new(VideoId, privateTitle);

        Assert.Equal(typeof(VideoMetadata).FullName, metadata.ToString());
        Assert.DoesNotContain(VideoId, metadata.ToString()!);
        Assert.DoesNotContain(privateTitle, metadata.ToString()!);
        Assert.DoesNotContain(CanonicalUrl, metadata.ToString()!);

        ArgumentException idError = Assert.Throws<ArgumentException>(() => new VideoMetadata(invalidId, privateTitle));
        ArgumentException titleError = Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, privateTitle + "\n"));
        ArgumentException providerError = Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, privateTitle, invalidProvider));
        ArgumentException thumbnailError = Assert.Throws<ArgumentException>(() => new VideoMetadata(VideoId, privateTitle, thumbnailUrl: invalidThumbnail));

        Assert.DoesNotContain(invalidId, idError.Message);
        Assert.DoesNotContain(privateTitle, titleError.Message);
        Assert.DoesNotContain(invalidProvider, providerError.Message);
        Assert.DoesNotContain(invalidThumbnail.AbsoluteUri, thumbnailError.Message);
        Assert.All(new[] { idError, titleError, providerError, thumbnailError }, error => Assert.Null(error.InnerException));
    }
}
