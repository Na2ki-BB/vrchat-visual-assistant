using System.IO;
using System.Net;
using System.Net.Http;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Settings;
using VrcVa.Windows.Translation;

namespace VrcVa.Windows.Tests;

public sealed class UsageSettingsSnapshotTests
{
    [Fact]
    public void Reload_ChangesCeilingsWithoutReplacingCountersOrResettingConsumption()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            VrcVaSettingsStore store = new(Path.Combine(directory, "settings.json"));
            FeatureUsageQuotas quotas = new();
            UsageSettingsSnapshot snapshot = new(quotas);
            TextRequestQuota translation = quotas.Translation;
            TextRequestQuota interpretation = quotas.SearchInterpretation;
            Consume(translation, 3);
            Consume(interpretation, 2);

            VrcVaSettings settings = VrcVaSettings.Default with
            {
                UsageLimits = new FeatureUsageLimits
                {
                    TranslationRequests = 2,
                    SearchInterpretationRequests = 6,
                },
                VoiceInput = new VoiceInputOptions { MaximumRecordingSeconds = 12 },
            };
            store.Save(settings);
            Assert.Equal(settings, snapshot.Reload(store));
            Assert.Same(translation, quotas.Translation);
            Assert.Same(interpretation, quotas.SearchInterpretation);
            Assert.Equal(0, translation.Remaining);
            Assert.Equal(4, interpretation.Remaining);
            Assert.Equal(12, snapshot.VoiceInput.MaximumRecordingSeconds);

            store.Save(settings with
            {
                UsageLimits = settings.UsageLimits with
                {
                    TranslationRequests = 8,
                    SearchInterpretationRequests = 1,
                },
            });
            snapshot.Reload(store);
            Assert.Equal(5, translation.Remaining);
            Assert.Equal(0, interpretation.Remaining);
            Assert.Equal(3, translation.Consumed);
            Assert.Equal(2, interpretation.Consumed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reload_InvalidOrUnreadableFilePreservesLastGoodSnapshotAndCounters(bool unreadable)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            VrcVaSettingsStore store = new(path);
            FeatureUsageQuotas quotas = new();
            UsageSettingsSnapshot snapshot = new(quotas);
            VrcVaSettings settings = VrcVaSettings.Default with
            {
                UsageLimits = new FeatureUsageLimits { TranslationRequests = 4 },
                VoiceInput = new VoiceInputOptions { MaximumRecordingSeconds = 9 },
            };
            store.Save(settings);
            snapshot.Reload(store);
            Consume(quotas.Translation, 2);
            using FileStream? exclusive = unreadable
                ? File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : null;
            if (!unreadable)
            {
                string json = File.ReadAllText(path).Replace(
                    "\"SearchInterpretationRequests\": 10", "\"SearchInterpretationRequests\": 0",
                    StringComparison.Ordinal);
                File.WriteAllText(path, json);
            }

            if (unreadable) { Assert.ThrowsAny<IOException>(() => snapshot.Reload(store)); }
            else { Assert.Throws<InvalidDataException>(() => snapshot.Reload(store)); }
            Assert.Equal(settings.UsageLimits, snapshot.UsageLimits);
            Assert.Equal(settings.VoiceInput, snapshot.VoiceInput);
            Assert.Equal(2, quotas.Translation.Consumed);
            Assert.Equal(2, quotas.Translation.Remaining);
            Assert.Equal(10, quotas.SearchInterpretation.Remaining);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reload_ReplacedDirectoryOrDeletedFileCannotRaisePreviousCeiling(bool directoryReplacement)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            VrcVaSettingsStore store = new(path);
            FeatureUsageQuotas quotas = new();
            UsageSettingsSnapshot snapshot = new(quotas);
            store.Save(VrcVaSettings.Default with
            {
                UsageLimits = new FeatureUsageLimits { TranslationRequests = 2 },
            });
            snapshot.Reload(store);
            Consume(quotas.Translation, 2);
            File.Delete(path);
            if (directoryReplacement) { Directory.CreateDirectory(path); }

            Assert.ThrowsAny<Exception>(() => snapshot.Reload(store));
            Assert.Equal(2, quotas.Translation.Maximum);
            Assert.Equal(2, quotas.Translation.Consumed);
            Assert.Equal(0, quotas.Translation.Remaining);
            if (directoryReplacement) { Directory.Delete(path); }
            store.Save(VrcVaSettings.Default with
            {
                UsageLimits = new FeatureUsageLimits { TranslationRequests = 4 },
            });
            snapshot.Reload(store);
            Assert.Equal(2, quotas.Translation.Remaining);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Reload_GenuinelyMissingInitialFileKeepsDefaultsAcrossOperations()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            VrcVaSettingsStore store = new(Path.Combine(directory, "new", "settings.json"));
            FeatureUsageQuotas quotas = new();
            UsageSettingsSnapshot snapshot = new(quotas);
            Assert.Equal(VrcVaSettings.Default, snapshot.Reload(store));
            Consume(quotas.Translation, 1);
            Assert.Equal(VrcVaSettings.Default, snapshot.Reload(store));
            Assert.Equal(9, quotas.Translation.Remaining);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Apply_InvalidVoicePreferenceRejectsEntireSnapshot()
    {
        FeatureUsageQuotas quotas = new();
        UsageSettingsSnapshot snapshot = new(quotas);
        Consume(quotas.Translation, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Apply(VrcVaSettings.Default with
        {
            UsageLimits = new FeatureUsageLimits { TranslationRequests = 50 },
            VoiceInput = new VoiceInputOptions { MaximumRecordingSeconds = 0 },
        }));
        Assert.Equal(new FeatureUsageLimits(), snapshot.UsageLimits);
        Assert.Equal(new VoiceInputOptions(), snapshot.VoiceInput);
        Assert.Equal(9, quotas.Translation.Remaining);
    }

    [Fact]
    public async Task RuntimeReplacementAndModelChangeKeepBothIndependentAllowances()
    {
        RecordingHandler handler = new();
        using HttpClient http = new(handler);
        FeatureUsageQuotas quotas = new(new FeatureUsageLimits
        {
            TranslationRequests = 2,
            SearchInterpretationRequests = 3,
        });
        TranslationRuntimeFactory factory = new(http, new FixedOcr(), quotas.Translation);
        TranslationRuntime first = CreateRuntime(factory);
        await first.OpenAiTranslator!.TranslateToJapaneseAsync("First", CancellationToken.None);
        OpenAiResponsesTextModelClient interpretation = new(http, new OpenAiTranslatorOptions(),
            "test-placeholder-key", quotas.SearchInterpretation);
        await interpretation.GenerateAsync(new TextModelRequest(OpenAiTranslatorOptions.BudgetModel,
            "Test interpretation.", "Original transcript", 50), CancellationToken.None);

        TranslationRuntime replacement = CreateRuntime(factory);
        replacement.OpenAiTranslator!.SelectModel(OpenAiTranslatorOptions.BudgetModel);
        Assert.Equal(1, replacement.OpenAiTranslator.RemainingRequests);
        await replacement.OpenAiTranslator.TranslateToJapaneseAsync("Second", CancellationToken.None);
        TranslationRuntimeFactory rebuiltFactory = new(http, new FixedOcr(), quotas.Translation);
        TranslationRuntime rebuilt = CreateRuntime(rebuiltFactory);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() =>
            rebuilt.OpenAiTranslator!.TranslateToJapaneseAsync("Excess", CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationUsageLimitReached, failure.FailureCode);
        Assert.Equal(0, quotas.Translation.Remaining);
        Assert.Equal(2, quotas.SearchInterpretation.Remaining);
        Assert.Equal(3, handler.SendCount);
        Assert.Contains("残り0/2回", rebuilt.Status, StringComparison.Ordinal);
        Assert.Equal(OpenAiTranslatorOptions.QualityModel, rebuilt.OpenAiTranslator!.Model);
    }

    [Fact]
    public async Task SharedExecutionGateRejectsAnotherPurposeAndReloadDuringSend()
    {
        ExecutionCoordinator execution = new();
        FeatureUsageQuotas quotas = new();
        BlockingHandler handler = new();
        using HttpClient http = new(handler);
        OpenAiResponsesTextModelClient translation = new(http, new OpenAiTranslatorOptions(),
            "test-placeholder-key", quotas.Translation);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        Task<TextModelResponse> running = translation.GenerateAsync(
            new TextModelRequest(OpenAiTranslatorOptions.DefaultModel, "Test.", "Input", 50),
            operation.CancellationToken);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out _));
        Assert.False(execution.TryBeginConfiguration(out _));
        Assert.False(execution.TryUpdateWhenIdle(() => quotas.ApplyLimits(
            new FeatureUsageLimits { TranslationRequests = 20 })));
        Assert.Equal(10, quotas.Translation.Maximum);
        Assert.Equal(10, quotas.SearchInterpretation.Remaining);
        handler.Release.TrySetResult();
        await running;
        operation.Dispose();
        Assert.True(execution.TryUpdateWhenIdle(() => quotas.ApplyLimits(
            new FeatureUsageLimits { TranslationRequests = 20 })));
        Assert.Equal(19, quotas.Translation.Remaining);
        Assert.Equal(1, handler.SendCount);
    }

    private static TranslationRuntime CreateRuntime(TranslationRuntimeFactory factory) =>
        factory.Create("openai", "test-placeholder-key", null, () => new OpenAiTranslatorOptions());

    private static void Consume(TextRequestQuota quota, int count)
    {
        for (int index = 0; index < count; index++)
        {
            Assert.True(quota.TryReserve(out TextRequestQuota.TextRequestReservation? reservation));
            using (reservation) { reservation.MarkSendStarted(CancellationToken.None); }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vrcva-usage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FixedOcr : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput("Test", "en"));
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"output":[{"content":[{"type":"output_text","text":"Test result"}]}]}"""),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(Response());
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Response();
        }
    }
}
