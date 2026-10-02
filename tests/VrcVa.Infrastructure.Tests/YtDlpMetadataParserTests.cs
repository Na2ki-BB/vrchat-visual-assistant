using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class YtDlpMetadataParserTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(10)]
    public void Parse_BoundedBatchIncludesOrdinaryCanonicalUrls(int count)
    {
        VideoSearchBatch batch = Parse(new
        {
            entries = Enumerable.Range(0, count).Select(index =>
            new
            {
                id = $"sample{index:00000}",
                title = "Self-authored title <literal>",
                url = $"https://www.youtube.com/watch?v=sample{index:00000}&list=ignored"
            })
        });
        Assert.Equal(count, batch.Videos.Count);
        Assert.False(batch.IsPartial);
        Assert.All(batch.Videos, value => Assert.Equal(
            "https://www.youtube.com/watch?v=" + value.VideoId, value.WatchUrl.AbsoluteUri));
    }

    [Fact]
    public void Parse_FlatExtractorFieldsAndOptionalThumbnailRequireNoExtraExtraction()
    {
        VideoSearchBatch batch = Parse(new
        {
            entries = new[] { new {
            _type = "url", ie_key = "Youtube", id = "sample00000", title = "Synthetic title",
            url = "https://www.youtube.com/watch?v=sample00000",
            thumbnails = new[] { new { url = "https://i.ytimg.com/vi/sample00000/hqdefault.jpg" } },
        } }
        });
        Assert.Equal("https://i.ytimg.com/vi/sample00000/hqdefault.jpg", Assert.Single(batch.Videos).ThumbnailUrl!.AbsoluteUri);
    }

    [Fact]
    public void Parse_SignedThumbnailIsValidatedThenCanonicalizedToBoundedJpegUrl()
    {
        VideoSearchBatch batch = Parse(new
        {
            entries = new[] { new {
                id = "sample00000", title = "Synthetic title",
                thumbnails = new[] { new {
                    url = "https://i.ytimg.com/vi/sample00000/hq720.jpg?sqp=provider-token&rs=provider-signature",
                    width = 360, height = 202,
                } },
            } }
        });

        Assert.False(batch.IsPartial);
        Assert.Equal("https://i.ytimg.com/vi/sample00000/hqdefault.jpg",
            Assert.Single(batch.Videos).ThumbnailUrl!.AbsoluteUri);
    }

    [Fact]
    public void Parse_FiltersInvalidAndDuplicateEntriesButKeepsFirstValidSnapshot()
    {
        VideoSearchBatch batch = Parse(new
        {
            entries = new object[] {
            new { id = "sample00000", title = "First" }, new { id = "sample00000", title = "Duplicate" },
            new { id = "invalid", title = "Bad" }, 7, new { id = "sample00001", title = "Second" },
        }
        });
        Assert.True(batch.IsPartial);
        Assert.Equal(new[] { "First", "Second" }, batch.Videos.Select(value => value.Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json private output")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"entries\":null}")]
    [InlineData("{\"entries\":{}}")]
    [InlineData("{\"entries\":[],\"entries\":[]}")]
    [InlineData("{\"entries\":[null]}")]
    [InlineData("{\"entries\":[{\"id\":\"sample00000\",\"title\":7}]}")]
    [InlineData("{\"entries\":[{\"id\":\"sample00000\",\"title\":\"ok\",\"id\":\"sample00001\"}]}")]
    [InlineData("{\"entries\":[]}{}")]
    public void Parse_RejectsBadResponseWithoutEchoingContent(string json)
    {
        ScanException failure = Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ScanFailureCode.VideoSearchInvalidMetadata, failure.FailureCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private output", failure.ToString());
    }

    [Fact]
    public void Parse_RejectsExcessCountBytesAndDepth()
    {
        Assert.Throws<ScanException>(() => Parse(new
        {
            entries = Enumerable.Range(0, 11).Select(index =>
            new { id = $"sample{index:00000}", title = "Title" })
        }));
        Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(new byte[YtDlpVideoSearchProvider.MaximumStdoutBytes + 1]));
        Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes(
            "{\"entries\":[],\"extra\":" + new string('[', 34) + "0" + new string(']', 34) + "}")));
        Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(new byte[] { 0xff, 0xfe }));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=sample00001")]
    [InlineData("http://www.youtube.com/watch?v=sample00000")]
    [InlineData("https://evil.test/watch?v=sample00000")]
    [InlineData("file:///sample00000")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://www.youtube.com/watch?v=sample00000&v=sample00000")]
    [InlineData("https://user@www.youtube.com/watch?v=sample00000")]
    [InlineData("https://www.youtube.com:444/watch?v=sample00000")]
    [InlineData("https://www.youtube.com/other/../watch?v=sample00000")]
    [InlineData("https://www.youtube.com/watch?v=sample00000#fragment")]
    public void Parse_RejectsUntrustedOrMismatchingProviderUrl(string url)
    {
        Assert.Throws<ScanException>(() => Parse(new { entries = new[] { new { id = "sample00000", title = "Title", url } } }));
        Assert.Throws<ScanException>(() => Parse(new
        {
            entries = new[] { new {
            id = "sample00000", title = "Title", url = "https://youtu.be/sample00000", webpage_url = url,
        } }
        }));
    }

    [Theory]
    [InlineData("https://youtube.com/watch?v=sample00000&feature=ignored")]
    [InlineData("https://www.youtube.com:443/watch?v=sample00000")]
    [InlineData("https://youtu.be/sample00000?t=12")]
    public void Parse_NormalizesAllowedProviderUrls(string url) => Assert.Equal(
        "https://www.youtube.com/watch?v=sample00000", Assert.Single(Parse(new
        {
            entries = new[] { new {
            id = "sample00000", title = "Title", url,
        } }
        }).Videos).WatchUrl.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("x\u2028y")]
    [InlineData("x\u0000y")]
    public void Parse_RejectsInvalidTitle(string title) => Assert.Throws<ScanException>(() =>
        Parse(new { entries = new[] { new { id = "sample00000", title } } }));

    [Fact]
    public void Parse_TitleByteLimitDoesNotTruncate()
    {
        string title = new string('あ', 1333) + "a";
        Assert.Equal(title, Assert.Single(Parse(new { entries = new[] { new { id = "sample00000", title } } }).Videos).Title);
        Assert.Throws<ScanException>(() => Parse(new { entries = new[] { new { id = "sample00000", title = title + "b" } } }));
    }

    [Theory]
    [InlineData("https://evil.test/image.jpg")]
    [InlineData("http://i.ytimg.com/image.jpg")]
    [InlineData("https://i.ytimg.com:444/image.jpg")]
    [InlineData("https://user@i.ytimg.com/image.jpg")]
    [InlineData("https://i.ytimg.com/image.jpg#bad")]
    public void Parse_BadOptionalThumbnailKeepsSelectableTitleWithPartialStatus(string thumbnail)
    {
        VideoSearchBatch batch = Parse(new { entries = new[] { new { id = "sample00000", title = "Title", thumbnail } } });
        Assert.True(batch.IsPartial);
        Assert.Null(Assert.Single(batch.Videos).ThumbnailUrl);
    }

    [Fact]
    public void Parse_InvalidOptionalFieldTypeCannotSmuggleContent()
    {
        Assert.Throws<ScanException>(() => Parse(new { entries = new[] { new { id = "sample00000", title = "Title", url = 1 } } }));
        VideoSearchBatch batch = Parse(new { entries = new[] { new { id = "sample00000", title = "Title", thumbnail = 1 } } });
        Assert.True(batch.IsPartial);
        Assert.Null(Assert.Single(batch.Videos).ThumbnailUrl);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("url")]
    public void Parse_LoneSurrogateCandidateIsFilteredWithValidSibling(string field)
    {
        string bad = "{\"id\":\"sample00001\",\"title\":\"Title\"}";
        if (field == "id") { bad = "{\"id\":\"\\uD800\",\"title\":\"Title\"}"; }
        if (field == "title") { bad = "{\"id\":\"sample00001\",\"title\":\"\\uDC00\"}"; }
        if (field == "url") { bad = "{\"id\":\"sample00001\",\"title\":\"Title\",\"url\":\"\\uD800\"}"; }
        VideoSearchBatch batch = YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes(
            "{\"entries\":[" + bad + ",{\"id\":\"sample00000\",\"title\":\"Good\"}]}"));
        Assert.True(batch.IsPartial);
        Assert.Equal("Good", Assert.Single(batch.Videos).Title);
    }

    [Fact]
    public void Parse_LoneSurrogateOptionalThumbnailIsDiscardedAndRootPropertyIsTypedFailure()
    {
        VideoSearchBatch batch = YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes(
            "{\"entries\":[{\"id\":\"sample00000\",\"title\":\"Title\",\"thumbnail\":\"\\uD800\"}]}"));
        Assert.True(batch.IsPartial);
        Assert.Null(Assert.Single(batch.Videos).ThumbnailUrl);
        ScanException failure = Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes(
            "{\"entries\":[],\"\\uD800\":0}")));
        Assert.Equal(ScanFailureCode.VideoSearchInvalidMetadata, failure.FailureCode);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void Parse_InvalidUtf8InIgnoredFieldIsRejectedAsTypedFailure()
    {
        byte[] before = Encoding.UTF8.GetBytes("{\"entries\":[],\"ignored\":\"");
        byte[] after = Encoding.UTF8.GetBytes("\"}");
        ScanException failure = Assert.Throws<ScanException>(() => YtDlpMetadataParser.Parse(
            before.Concat(new byte[] { 0xff }).Concat(after).ToArray()));
        Assert.Equal(ScanFailureCode.VideoSearchInvalidMetadata, failure.FailureCode);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void Parse_ProviderWarningPropagatesAsPartialStatusWithoutStderrContent() => Assert.True(
        YtDlpMetadataParser.Parse(Encoding.UTF8.GetBytes("{\"entries\":[]}"), hasProviderWarning: true).IsPartial);

    private static VideoSearchBatch Parse(object value) => YtDlpMetadataParser.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
}
