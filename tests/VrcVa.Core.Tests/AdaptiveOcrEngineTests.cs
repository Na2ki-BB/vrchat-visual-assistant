namespace VrcVa.Core.Tests;

public sealed class AdaptiveOcrEngineTests
{
    [Fact]
    public async Task RecognizeAsync_SkipsEnhancementWhenPrimaryTextIsStrong()
    {
        QueueOcrEngine primary = new(
            new OcrOutput(new string('A', 80), "en-US"));
        RecordingRegionSource regions = new([]);
        AdaptiveOcrEngine engine = new(primary, regions);
        using CapturedFrame frame = CreateFrame(1);

        OcrOutput output = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal(new string('A', 80), output.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, regions.CallCount);
    }

    [Fact]
    public async Task RecognizeAsync_UsesBetterOverlappingRegionTextAndRemovesDuplicates()
    {
        QueueOcrEngine primary = new(
            new OcrOutput("EXIT", "en-US"),
            new OcrOutput("KEEP OUT\nEmergency exit", "en-US"),
            new OcrOutput(" emergency   exit \nAuthorized personnel only", "en-US"),
            new OcrOutput("AUTHORIZED PERSONNEL ONLY", "en-US"));
        RecordingRegionSource regions = new(
            [CreateFrame(2), CreateFrame(3), CreateFrame(4)]);
        AdaptiveOcrEngine engine = new(primary, regions);
        using CapturedFrame frame = CreateFrame(1);

        OcrOutput output = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "KEEP OUT",
                "Emergency exit",
                "Authorized personnel only"),
            output.Text);
        Assert.Contains("OCR強化処理", output.Warning, StringComparison.Ordinal);
        Assert.All(regions.Frames, region =>
            Assert.Throws<ObjectDisposedException>(() => region.EncodedImage.ToArray()));
    }

    [Fact]
    public async Task RecognizeAsync_KeepsPrimaryTextWhenRegionsAreNotBetter()
    {
        QueueOcrEngine primary = new(
            new OcrOutput("EMERGENCY EXIT AHEAD", "en-US"),
            new OcrOutput("EXIT", "en-US"));
        RecordingRegionSource regions = new([CreateFrame(2)]);
        AdaptiveOcrEngine engine = new(primary, regions);
        using CapturedFrame frame = CreateFrame(1);

        OcrOutput output = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal("EMERGENCY EXIT AHEAD", output.Text);
        Assert.Null(output.Warning);
    }

    [Fact]
    public void Score_CountsOnlyAsciiLettersAndDigits()
    {
        Assert.Equal(5, AdaptiveOcrEngine.Score("Ab c-12_日本語"));
    }

    private static CapturedFrame CreateFrame(byte marker) =>
        new([marker], 100, 100, "image/png", "test");

    private sealed class QueueOcrEngine(params OcrOutput[] outputs) : IOcrEngine
    {
        private readonly Queue<OcrOutput> _outputs = new(outputs);

        public int CallCount { get; private set; }

        public Task<OcrOutput> RecognizeAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_outputs.Dequeue());
        }
    }

    private sealed class RecordingRegionSource(IReadOnlyList<CapturedFrame> frames)
        : IOcrRegionSource
    {
        public IReadOnlyList<CapturedFrame> Frames { get; } = frames;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<CapturedFrame>> CreateRegionsAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Frames);
        }
    }
}
