namespace VrcVa.Core.Tests;

public sealed class SearchQueryInterpretationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("one\ntwo")]
    [InlineData("one\rtwo")]
    [InlineData("one\ttwo")]
    [InlineData("one\0two")]
    [InlineData("one\u2028two")]
    [InlineData("one\u2029two")]
    [InlineData("one\u200btwo")]
    [InlineData("https://www.youtube.com/watch?v=AAAAAAAAAAA")]
    [InlineData("See www.example.com")]
    [InlineData("youtu.be/AAAAAAAAAAA")]
    [InlineData("youtu.be")]
    [InlineData("YOUTU.BE")]
    [InlineData("example.xn--p1ai/video")]
    [InlineData("例え.テスト/video")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///tmp/test")]
    [InlineData("example.com/video")]
    [InlineData("example.xyz/video")]
    [InlineData("example.edu/video")]
    [InlineData("example.xyz")]
    [InlineData("127.0.0.1/video")]
    [InlineData("127.0.0.1?video=1")]
    [InlineData("localhost/video")]
    [InlineData("example.museum/video")]
    [InlineData("example.museum?video=1")]
    [InlineData("custom://resource")]
    [InlineData("custom:/resource")]
    [InlineData("\"sample query\"")]
    [InlineData("「sample query」")]
    [InlineData("`sample`")]
    [InlineData("{\"query\":\"sample\"}")]
    [InlineData("[sample]")]
    public void InvalidOutput_IsTypedFailure(string output)
    {
        ScanException exception = Assert.Throws<ScanException>(() => new SearchQueryInterpretation(output));
        Assert.Equal(ScanStage.SearchInterpretation, exception.Stage);
        Assert.Equal(ScanFailureCode.SearchInterpretationInvalidResponse, exception.FailureCode);
    }

    [Theory]
    [InlineData("RE:MAKE 2024 live")]
    [InlineData("ONE PIECE: RED")]
    [InlineData("Mr.Children 2024 live")]
    [InlineData("Artist.Name 2024 live")]
    public void TitleColons_AreNotMistakenForUrls(string query)
    {
        Assert.Equal(query, new SearchQueryInterpretation(query).Query);
    }

    [Fact]
    public void Bounds_UseUtf8AndRejectInvalidSurrogatesWithoutTruncation()
    {
        Assert.Equal(new string('x', 1_000), new SearchQueryInterpretation(new string('x', 1_000)).Query);
        Assert.Equal(new string('界', 333), new SearchQueryInterpretation(new string('界', 333)).Query);
        Assert.Throws<ScanException>(() => new SearchQueryInterpretation(new string('x', 1_001)));
        Assert.Throws<ScanException>(() => new SearchQueryInterpretation(new string('界', 334)));
        Assert.Throws<ScanException>(() => new SearchQueryInterpretation("before\ud800after"));
        Assert.Throws<ScanException>(() => new SearchQueryInterpretation("before\udc00after"));
        Assert.Equal("曲 🎵 2024 ライブ", new SearchQueryInterpretation("  曲 🎵 2024 ライブ  ").Query);
    }
}
