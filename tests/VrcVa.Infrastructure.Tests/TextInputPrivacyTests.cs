using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class TextInputPrivacyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipelineLogs_DoNotContainTranscriptResultOrExceptionMessage(bool fail)
    {
        const string transcript = "private-transcript-marker";
        const string query = "private-query-marker";
        const string exceptionMessage = "private-exception-marker";
        string directory = Path.Combine(Path.GetTempPath(), $"vrcva-text-privacy-{Guid.NewGuid():N}");
        try
        {
            FeatureId id = new("test.text-privacy");
            ScanPipeline pipeline = new(
                new NoCapture(),
                new FeatureCatalog(new FeatureEntry(
                    new FeatureDescriptor(id, "Text", FeatureInputKind.Text, FeatureDataBoundary.LocalOnly),
                    new FakeHandler(query, fail, exceptionMessage))),
                new NoRendering(),
                new PrivacySafeFileLogger(directory));

            ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.CreateText(
                "test", id, TextInputSession.Create(transcript)));
            string logs = string.Concat(Directory.GetFiles(directory).Select(File.ReadAllText));

            Assert.Equal(!fail, outcome.IsSuccess);
            Assert.Contains("stage=TextHandling", logs);
            Assert.DoesNotContain(transcript, logs);
            Assert.DoesNotContain(query, logs);
            Assert.DoesNotContain(exceptionMessage, logs);
            Assert.DoesNotContain("capture.completed", logs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class NoCapture : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Text input must not capture.");
    }

    private sealed class FakeHandler(string query, bool fail, string exceptionMessage) : ITextFeatureHandler
    {
        public Task<FeatureResult> HandleAsync(TextInputSession input, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            return Task.FromResult(new FeatureResult(request.FeatureId,
            [
                new ResultSection("transcript", "Transcript", input.Transcript),
                new ResultSection("query", "Query", query, ResultSectionRole.Primary),
            ]));
        }
    }

    private sealed class NoRendering : IResultRenderer
    {
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
