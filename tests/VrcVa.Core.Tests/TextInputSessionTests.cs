namespace VrcVa.Core.Tests;

public sealed class TextInputSessionTests
{
    [Fact]
    public void Session_PreservesOriginalTextAndUsesDistinctIdentity()
    {
        const string transcript = "  sample\r\n日本語\t  ";
        TextInputSession first = TextInputSession.Create(transcript);
        TextInputSession second = TextInputSession.Create(transcript);

        Assert.Equal(transcript, first.Transcript);
        Assert.NotEqual(Guid.Empty, first.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.DoesNotContain(transcript, first.ToString()!);
    }

    [Theory]
    [InlineData(4_000)]
    [InlineData(1)]
    public void Transcript_AcceptsInclusiveAsciiByteLimit(int bytes)
    {
        TextInputSession session = TextInputSession.Create(new string('a', bytes));
        Assert.Equal(bytes, session.Transcript.Length);
    }

    [Fact]
    public void Transcript_EnforcesUtf8BytesWithoutTruncation()
    {
        string allowed = new string('あ', 1_333) + "a";
        Assert.Equal(allowed, TextInputSession.Create(allowed).Transcript);
        Assert.Throws<ArgumentException>(() => TextInputSession.Create(allowed + "b"));
        Assert.Throws<ArgumentException>(() => TextInputSession.Create(new string('a', 4_001)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Transcript_RejectsEmptyInput(string input)
    {
        Assert.Throws<ArgumentException>(() => TextInputSession.Create(input));
    }

    [Fact]
    public void Session_RejectsNullTextAndEmptyIdentity()
    {
        Assert.Throws<ArgumentNullException>(() => TextInputSession.Create(null!));
        Assert.Throws<ArgumentException>(() => new TextInputSession(Guid.Empty, "text"));
        Assert.Throws<ArgumentNullException>(() => ScanRequest.CreateText("test", FeatureIds.Translation, null!));
    }

    [Theory]
    [InlineData(FeatureInputKind.Text)]
    [InlineData((FeatureInputKind)99)]
    public void ImageHandler_RejectsDifferentInputKind(FeatureInputKind inputKind)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FeatureEntry(
            new FeatureDescriptor(new FeatureId("test.image"), "Image", inputKind, FeatureDataBoundary.LocalOnly),
            new OcrAnalyzer(new FakeOcr())));
    }

    [Fact]
    public void TextHandler_RejectsImageDescriptor()
    {
        Assert.Throws<ArgumentException>(() => new FeatureEntry(BuiltInFeatures.Translation, new FakeTextHandler()));
    }

    [Fact]
    public void TextEntry_ContainsOnlyMatchingHandler()
    {
        FakeTextHandler handler = new();
        FeatureEntry entry = new(new FeatureDescriptor(new FeatureId("test.text"), "Text", FeatureInputKind.Text, FeatureDataBoundary.LocalOnly), handler);

        Assert.Same(handler, entry.TextHandler);
        Assert.Null(entry.Analyzer);
        FeatureEntry image = new(BuiltInFeatures.Translation, new OcrAnalyzer(new FakeOcr()));
        Assert.NotNull(image.Analyzer);
        Assert.Null(image.TextHandler);
    }

    [Fact]
    public void Boundaries_DistinguishAudioAndBothTextDestinations()
    {
        FeatureDataBoundary search = FeatureDataBoundary.InputTextToOpenAi | FeatureDataBoundary.SearchTextToYouTube;
        FeatureDescriptor descriptor = new(new FeatureId("test.search"), "Search", FeatureInputKind.Text, search);

        Assert.True(descriptor.DataBoundary.HasFlag(FeatureDataBoundary.InputTextToOpenAi));
        Assert.True(descriptor.DataBoundary.HasFlag(FeatureDataBoundary.SearchTextToYouTube));
        Assert.False(descriptor.DataBoundary.HasFlag(FeatureDataBoundary.VoiceAudioToOpenAi));
        Assert.NotEqual(FeatureDataBoundary.VoiceAudioToOpenAi, FeatureDataBoundary.InputTextToOpenAi);
        Assert.Equal(FeatureDataBoundary.ExtractedTextMayLeaveDevice, BuiltInFeatures.Translation.DataBoundary);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureDescriptor(
            new FeatureId("test.invalid"), "Invalid", FeatureInputKind.Text, (FeatureDataBoundary)32));
    }

    private sealed class FakeOcr : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput("source", "en"));
    }

    private sealed class FakeTextHandler : ITextFeatureHandler
    {
        public Task<FeatureResult> HandleAsync(TextInputSession input, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This handler must not be called.");
    }
}
