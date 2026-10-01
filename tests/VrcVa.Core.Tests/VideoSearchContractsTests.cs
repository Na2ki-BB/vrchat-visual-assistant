namespace VrcVa.Core.Tests;

public sealed class VideoSearchContractsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Query_RejectsBlankBeforeProviderWork(string query) =>
        Assert.Throws<ArgumentException>(() => new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), query));

    [Fact]
    public void Query_RejectsNullIdsAndByteExcessWithoutTruncation()
    {
        Assert.Throws<ArgumentNullException>(() => new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), null!));
        Assert.Throws<ArgumentException>(() => new VideoSearchRequest(Guid.Empty, Guid.NewGuid(), "query"));
        Assert.Throws<ArgumentException>(() => new VideoSearchRequest(Guid.NewGuid(), Guid.Empty, "query"));
        string exact = new string('あ', 1_333) + "a";
        Assert.Equal(exact, new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), exact).Query);
        Assert.Throws<ArgumentException>(() => new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), exact + "b"));
        Assert.Throws<ArgumentException>(() => new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), new string('a', 4_001)));
        Assert.Equal(4_000, new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), new string('a', 4_000)).Query.Length);
    }

    [Fact]
    public void Batch_IsBoundedDistinctAndCopiedFromProviderCollection()
    {
        VideoMetadata video = new("sample00000", "Synthetic title");
        List<VideoMetadata> videos = [video];
        VideoSearchBatch batch = new(videos);
        videos.Clear();
        Assert.Same(video, Assert.Single(batch.Videos));
        Assert.Empty(new VideoSearchBatch([]).Videos);
        Assert.Throws<ArgumentException>(() => new VideoSearchBatch([video, video]));
        Assert.Throws<ArgumentException>(() => new VideoSearchBatch([null!]));
        Assert.Throws<ArgumentNullException>(() => new VideoSearchBatch(null!));
        Assert.Throws<ArgumentException>(() => new VideoSearchBatch(Enumerable.Range(0, 11).Select(index =>
            new VideoMetadata($"sample{index:00000}", "Synthetic title"))));
        Assert.Throws<NotSupportedException>(() => ((IList<VideoMetadata>)batch.Videos).Clear());
    }

    [Fact]
    public void FaultyProviderEnumeration_StopsAtFirstExcessEntry()
    {
        int enumerated = 0;
        IEnumerable<VideoMetadata> Endless()
        {
            while (true)
            {
                enumerated++;
                yield return new VideoMetadata($"sample{enumerated:00000}", "Synthetic title");
            }
        }
        Assert.Throws<ArgumentException>(() => new VideoSearchBatch(Endless()));
        Assert.Equal(11, enumerated);
    }

    [Fact]
    public void ContentBearingContracts_DoNotEchoContentsInDefaultToString()
    {
        const string content = "private synthetic query";
        Assert.DoesNotContain(content, new VideoSearchRequest(Guid.NewGuid(), Guid.NewGuid(), content).ToString()!);
        Assert.DoesNotContain(content, new VideoSearchBatch([new VideoMetadata("sample00000", content)]).ToString()!);
    }
}
