using System.Collections.Concurrent;
using System.Net;
using System.Text;
using VrcVa.Core;
using TextRequestReservation = VrcVa.Infrastructure.TextRequestQuota.TextRequestReservation;

namespace VrcVa.Infrastructure.Tests;

public sealed class IndependentTextQuotaTests
{
    [Fact]
    public void Defaults_CreateDistinctTenAttemptPurposeBudgets()
    {
        FeatureUsageQuotas quotas = new();

        Assert.NotSame(quotas.Translation, quotas.SearchInterpretation);
        Assert.Equal(TextRequestPurpose.Translation, quotas.Translation.Purpose);
        Assert.Equal(TextRequestPurpose.SearchInterpretation, quotas.SearchInterpretation.Purpose);
        AssertQuota(quotas.Translation, maximum: 10, consumed: 0, remaining: 10);
        AssertQuota(quotas.SearchInterpretation, maximum: 10, consumed: 0, remaining: 10);
        Assert.Equal(300, quotas.Limits.VoiceSeconds);
        Assert.Equal(30, quotas.Limits.VoiceRequests);
        Assert.Equal(100, TextRequestQuota.HardMaximum);
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation, 1)]
    [InlineData(TextRequestPurpose.Translation, 10)]
    [InlineData(TextRequestPurpose.Translation, 100)]
    [InlineData(TextRequestPurpose.SearchInterpretation, 1)]
    [InlineData(TextRequestPurpose.SearchInterpretation, 10)]
    [InlineData(TextRequestPurpose.SearchInterpretation, 100)]
    public async Task GenerateAsync_BeforeAtAndBeyondLimitLeavesOtherPurposeAvailable(
        TextRequestPurpose purpose,
        int maximum)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        TextRequestQuota otherQuota = GetOtherQuota(quotas, purpose);
        ResponseHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiResponsesTextModelClient client = CreateClient(httpClient, quota);

        for (int index = 0; index < maximum; index++)
        {
            AssertQuota(quota, maximum, consumed: index, remaining: maximum - index);
            await client.GenerateAsync(CreateRequest(), CancellationToken.None);
        }

        AssertQuota(quota, maximum, consumed: maximum, remaining: 0);
        ScanException excess = await Assert.ThrowsAsync<ScanException>(() =>
            client.GenerateAsync(CreateRequest(), CancellationToken.None));

        AssertUsageFailure(excess, purpose);
        Assert.Equal(maximum, handler.SendCount);
        Assert.Equal(0, otherQuota.Consumed);
        OpenAiResponsesTextModelClient otherClient = CreateClient(httpClient, otherQuota);
        await otherClient.GenerateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(maximum + 1, handler.SendCount);
        Assert.Equal(1, otherQuota.Consumed);
        AssertQuota(quota, maximum, consumed: maximum, remaining: 0);
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public async Task GenerateAsync_ClientAndModelReplacementCannotResetOwnedBudget(
        TextRequestPurpose purpose)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 2);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        ResponseHandler firstHandler = new();
        ResponseHandler replacementHandler = new();
        using HttpClient firstHttpClient = new(firstHandler);
        using HttpClient replacementHttpClient = new(replacementHandler);
        OpenAiResponsesTextModelClient first = CreateClient(firstHttpClient, quota);
        OpenAiResponsesTextModelClient replacement = new(
            replacementHttpClient,
            CreateOptions() with
            {
                Model = OpenAiTranslatorOptions.BudgetModel,
                MaxRequestsPerSession = TextRequestQuota.HardMaximum,
            },
            "replacement-unit-test-key",
            quota);

        TextModelResponse firstResult = await first.GenerateAsync(
            CreateRequest(), CancellationToken.None);
        TextModelResponse replacementResult = await replacement.GenerateAsync(
            CreateRequest() with { Model = OpenAiTranslatorOptions.BudgetModel },
            CancellationToken.None);
        ScanException firstExcess = await Assert.ThrowsAsync<ScanException>(() =>
            first.GenerateAsync(CreateRequest(), CancellationToken.None));
        ScanException replacementExcess = await Assert.ThrowsAsync<ScanException>(() =>
            replacement.GenerateAsync(CreateRequest(), CancellationToken.None));

        AssertUsageFailure(firstExcess, purpose);
        AssertUsageFailure(replacementExcess, purpose);
        Assert.Equal(OpenAiTranslatorOptions.QualityModel, firstResult.Model);
        Assert.Equal(OpenAiTranslatorOptions.BudgetModel, replacementResult.Model);
        Assert.Equal(purpose, first.Purpose);
        Assert.Equal(purpose, replacement.Purpose);
        Assert.Equal(2, first.MaxRequestsPerSession);
        Assert.Equal(2, replacement.MaxRequestsPerSession);
        Assert.Equal(0, first.RemainingRequests);
        Assert.Equal(0, replacement.RemainingRequests);
        Assert.Equal(1, firstHandler.SendCount);
        Assert.Equal(1, replacementHandler.SendCount);
        Assert.Equal(0, GetOtherQuota(quotas, purpose).Consumed);
    }

    [Fact]
    public void ApplyLimits_LoweringAndRaisingPreservesCountersAndQuotaIdentity()
    {
        FeatureUsageQuotas quotas = new(new FeatureUsageLimits
        {
            TranslationRequests = 4,
            SearchInterpretationRequests = 3,
        });
        TextRequestQuota translation = quotas.Translation;
        TextRequestQuota interpretation = quotas.SearchInterpretation;
        Consume(translation, 3);
        Consume(interpretation, 2);
        FeatureUsageLimits lowered = new()
        {
            VoiceSeconds = 601,
            VoiceRequests = 61,
            TranslationRequests = 2,
            SearchInterpretationRequests = 1,
        };

        quotas.ApplyLimits(lowered);

        Assert.Same(lowered, quotas.Limits);
        Assert.Same(translation, quotas.Translation);
        Assert.Same(interpretation, quotas.SearchInterpretation);
        AssertQuota(translation, maximum: 2, consumed: 3, remaining: 0);
        AssertQuota(interpretation, maximum: 1, consumed: 2, remaining: 0);
        Assert.False(translation.TryReserve(out _));
        Assert.False(interpretation.TryReserve(out _));

        FeatureUsageLimits raised = lowered with
        {
            TranslationRequests = 5,
            SearchInterpretationRequests = 4,
        };
        quotas.ApplyLimits(raised);

        Assert.Same(raised, quotas.Limits);
        AssertQuota(translation, maximum: 5, consumed: 3, remaining: 2);
        AssertQuota(interpretation, maximum: 4, consumed: 2, remaining: 2);
        Consume(translation, 2);
        Consume(interpretation, 2);
        AssertQuota(translation, maximum: 5, consumed: 5, remaining: 0);
        AssertQuota(interpretation, maximum: 4, consumed: 4, remaining: 0);
        Assert.False(translation.TryReserve(out _));
        Assert.False(interpretation.TryReserve(out _));
    }

    [Theory]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), -1)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), 0)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), 3_601)]
    [InlineData(nameof(FeatureUsageLimits.VoiceSeconds), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), 301)]
    [InlineData(nameof(FeatureUsageLimits.VoiceRequests), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), 101)]
    [InlineData(nameof(FeatureUsageLimits.TranslationRequests), int.MaxValue)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), -1)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), 0)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), 101)]
    [InlineData(nameof(FeatureUsageLimits.SearchInterpretationRequests), int.MaxValue)]
    public void InvalidLimits_ConstructorRejectsAndApplyRollsBackEntireConfiguration(
        string field,
        int invalidValue)
    {
        FeatureUsageLimits original = new()
        {
            TranslationRequests = 4,
            SearchInterpretationRequests = 3,
        };
        FeatureUsageQuotas quotas = new(original);
        Consume(quotas.Translation, 1);
        Consume(quotas.SearchInterpretation, 2);
        using TextRequestReservation pending = Reserve(quotas.Translation);
        FeatureUsageLimits candidate = new()
        {
            VoiceSeconds = 600,
            VoiceRequests = 60,
            TranslationRequests = 5,
            SearchInterpretationRequests = 7,
        };
        candidate = field switch
        {
            nameof(FeatureUsageLimits.VoiceSeconds) => candidate with { VoiceSeconds = invalidValue },
            nameof(FeatureUsageLimits.VoiceRequests) => candidate with { VoiceRequests = invalidValue },
            nameof(FeatureUsageLimits.TranslationRequests) => candidate with { TranslationRequests = invalidValue },
            nameof(FeatureUsageLimits.SearchInterpretationRequests) => candidate with { SearchInterpretationRequests = invalidValue },
            _ => throw new ArgumentException("Unknown test limit field.", nameof(field)),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureUsageQuotas(candidate));
        Assert.Throws<ArgumentOutOfRangeException>(() => quotas.ApplyLimits(candidate));

        Assert.Same(original, quotas.Limits);
        AssertQuota(quotas.Translation, maximum: 4, consumed: 1, remaining: 2);
        AssertQuota(quotas.SearchInterpretation, maximum: 3, consumed: 2, remaining: 1);
        pending.Dispose();
        AssertQuota(quotas.Translation, maximum: 4, consumed: 1, remaining: 3);
    }

    [Fact]
    public void ApplyLimits_NullDoesNotChangeConfigurationOrConsumption()
    {
        FeatureUsageQuotas quotas = new();
        FeatureUsageLimits original = quotas.Limits;
        Consume(quotas.Translation, 1);

        Assert.Throws<ArgumentNullException>(() => quotas.ApplyLimits(null!));

        Assert.Same(original, quotas.Limits);
        AssertQuota(quotas.Translation, maximum: 10, consumed: 1, remaining: 9);
        AssertQuota(quotas.SearchInterpretation, maximum: 10, consumed: 0, remaining: 10);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void TextRequestQuota_InvalidMaximumIsRejected(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRequestQuota(maximum));
    }

    [Fact]
    public void TextRequestQuota_UnknownPurposeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TextRequestQuota(purpose: (TextRequestPurpose)int.MaxValue));
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public void Reservation_UnstartedDisposalReturnsCapacityOnceAndCannotBeStartedLater(
        TextRequestPurpose purpose)
    {
        TextRequestQuota quota = new(maximum: 1, purpose);
        TextRequestReservation reservation = Reserve(quota);
        AssertQuota(quota, maximum: 1, consumed: 0, remaining: 0);
        Assert.False(quota.TryReserve(out TextRequestReservation? excess));
        Assert.Null(excess);

        reservation.Dispose();
        reservation.Dispose();

        AssertQuota(quota, maximum: 1, consumed: 0, remaining: 1);
        Assert.Throws<InvalidOperationException>(() => reservation.MarkSendStarted(CancellationToken.None));
        using TextRequestReservation replacement = Reserve(quota);
        Assert.False(quota.TryReserve(out _));
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public void Reservation_StartedLeaseCannotBeReusedOrRolledBackByDisposal(
        TextRequestPurpose purpose)
    {
        TextRequestQuota quota = new(maximum: 1, purpose);
        TextRequestReservation reservation = Reserve(quota);
        reservation.MarkSendStarted(CancellationToken.None);

        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);
        Assert.Throws<InvalidOperationException>(() => reservation.MarkSendStarted(CancellationToken.None));
        reservation.Dispose();
        reservation.Dispose();

        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);
        Assert.Throws<InvalidOperationException>(() => reservation.MarkSendStarted(CancellationToken.None));
        Assert.False(quota.TryReserve(out _));
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public void Reservation_CancelledAfterReserveBeforeSendReturnsOnlyUnstartedCapacity(
        TextRequestPurpose purpose)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 1);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        TextRequestReservation reservation = Reserve(quota);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => reservation.MarkSendStarted(cancellation.Token));
        AssertQuota(quota, maximum: 1, consumed: 0, remaining: 0);
        reservation.Dispose();

        AssertQuota(quota, maximum: 1, consumed: 0, remaining: 1);
        Consume(quota, 1);
        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);
        Assert.Equal(0, GetOtherQuota(quotas, purpose).Consumed);
    }

    [Fact]
    public async Task ConcurrentReservations_IndependentCountersNeverOverbookOrLoseReturnedCapacity()
    {
        FeatureUsageQuotas quotas = new(new FeatureUsageLimits
        {
            TranslationRequests = 31,
            SearchInterpretationRequests = 37,
        });
        ConcurrentBag<TextRequestReservation> translations = [];
        ConcurrentBag<TextRequestReservation> interpretations = [];
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] contenders = Enumerable.Range(0, 512).Select(index => Task.Run(async () =>
        {
            await start.Task;
            TextRequestQuota quota = index % 2 == 0 ? quotas.Translation : quotas.SearchInterpretation;
            if (quota.TryReserve(out TextRequestReservation? reservation))
            {
                (index % 2 == 0 ? translations : interpretations).Add(reservation);
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(contenders);

        Assert.Equal(31, translations.Count);
        Assert.Equal(37, interpretations.Count);
        AssertQuota(quotas.Translation, maximum: 31, consumed: 0, remaining: 0);
        AssertQuota(quotas.SearchInterpretation, maximum: 37, consumed: 0, remaining: 0);

        await Task.WhenAll(
            StartOrReturnReservations(translations)
                .Concat(StartOrReturnReservations(interpretations)));

        AssertQuota(quotas.Translation, maximum: 31, consumed: 16, remaining: 15);
        AssertQuota(quotas.SearchInterpretation, maximum: 37, consumed: 19, remaining: 18);
        Consume(quotas.Translation, 15);
        Consume(quotas.SearchInterpretation, 18);
        AssertQuota(quotas.Translation, maximum: 31, consumed: 31, remaining: 0);
        AssertQuota(quotas.SearchInterpretation, maximum: 37, consumed: 37, remaining: 0);
        Assert.False(quotas.Translation.TryReserve(out _));
        Assert.False(quotas.SearchInterpretation.TryReserve(out _));
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public async Task Reservation_ConcurrentStartsCommitExactlyOneAttempt(TextRequestPurpose purpose)
    {
        TextRequestQuota quota = new(maximum: 2, purpose);
        using TextRequestReservation reservation = Reserve(quota);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int committed = 0;
        int rejected = 0;
        Task[] contenders = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                reservation.MarkSendStarted(CancellationToken.None);
                Interlocked.Increment(ref committed);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejected);
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(contenders);

        Assert.Equal(1, committed);
        Assert.Equal(63, rejected);
        reservation.Dispose();
        AssertQuota(quota, maximum: 2, consumed: 1, remaining: 1);
        Consume(quota, 1);
        Assert.False(quota.TryReserve(out _));
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public async Task Reservation_StartAndRepeatedDisposalRaceCannotDoubleCountOrOverReturn(
        TextRequestPurpose purpose)
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            TextRequestQuota quota = new(maximum: 2, purpose);
            TextRequestReservation reservation = Reserve(quota);
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> sender = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    reservation.MarkSendStarted(CancellationToken.None);
                    return 1;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            });
            Task[] disposers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                reservation.Dispose();
            })).ToArray();
            start.SetResult();
            await Task.WhenAll(disposers.Append(sender));
            int committed = await sender;

            AssertQuota(quota, maximum: 2, consumed: committed, remaining: 2 - committed);
            Assert.Throws<InvalidOperationException>(() => reservation.MarkSendStarted(CancellationToken.None));
            Consume(quota, 2 - committed);
            AssertQuota(quota, maximum: 2, consumed: 2, remaining: 0);
            Assert.False(quota.TryReserve(out _));
        }
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation, "http500")]
    [InlineData(TextRequestPurpose.Translation, "unauthorized")]
    [InlineData(TextRequestPurpose.Translation, "forbidden")]
    [InlineData(TextRequestPurpose.Translation, "rateLimited")]
    [InlineData(TextRequestPurpose.Translation, "connection")]
    [InlineData(TextRequestPurpose.Translation, "badJson")]
    [InlineData(TextRequestPurpose.Translation, "emptyOutput")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "http500")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "unauthorized")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "forbidden")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "rateLimited")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "connection")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "badJson")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "emptyOutput")]
    public async Task GenerateAsync_FailureAfterSendConsumesOnlyBoundPurposeAndDoesNotRetry(
        TextRequestPurpose purpose,
        string failure)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 2);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        ResponseHandler handler = new(failure);
        using HttpClient httpClient = new(handler);
        OpenAiResponsesTextModelClient client = CreateClient(httpClient, quota);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
                client.GenerateAsync(CreateRequest(), CancellationToken.None));
            ScanFailureCode expectedCode = failure switch
            {
                "unauthorized" or "forbidden" => ScanFailureCode.TranslationAuthenticationFailed,
                "rateLimited" => ScanFailureCode.TranslationRateLimited,
                _ => ScanFailureCode.TranslationFailed,
            };
            Assert.Equal(expectedCode, exception.FailureCode);
            Assert.Equal(GetStage(purpose), exception.Stage);
            Assert.Equal(attempt, handler.SendCount);
            AssertQuota(quota, maximum: 2, consumed: attempt, remaining: 2 - attempt);
            Assert.Equal(0, GetOtherQuota(quotas, purpose).Consumed);
        }

        ScanException excess = await Assert.ThrowsAsync<ScanException>(() =>
            client.GenerateAsync(CreateRequest(), CancellationToken.None));
        AssertUsageFailure(excess, purpose);
        Assert.Equal(2, handler.SendCount);
        Consume(GetOtherQuota(quotas, purpose), 1);
        AssertQuota(quota, maximum: 2, consumed: 2, remaining: 0);
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public async Task GenerateAsync_TimeoutAfterSendConsumesOnlyBoundPurpose(TextRequestPurpose purpose)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 1);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        WaitingHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiResponsesTextModelClient client = CreateClient(
            httpClient, quota, CreateOptions() with { Timeout = TimeSpan.FromMilliseconds(250) });

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            client.GenerateAsync(CreateRequest(), CancellationToken.None));

        Assert.True(handler.Entered.IsCompletedSuccessfully);
        Assert.Equal(ScanFailureCode.TranslationTimedOut, exception.FailureCode);
        Assert.Equal(GetStage(purpose), exception.Stage);
        Assert.Equal(1, handler.SendCount);
        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);
        Assert.Equal(0, GetOtherQuota(quotas, purpose).Consumed);
        Assert.False(quota.TryReserve(out _));
        Consume(GetOtherQuota(quotas, purpose), 1);
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation)]
    [InlineData(TextRequestPurpose.SearchInterpretation)]
    public async Task GenerateAsync_CallerCancellationAfterSendConsumesOnlyBoundPurpose(
        TextRequestPurpose purpose)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 1);
        TextRequestQuota quota = GetQuota(quotas, purpose);
        WaitingHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiResponsesTextModelClient client = CreateClient(httpClient, quota);
        using CancellationTokenSource cancellation = new();
        Task<TextModelResponse> pending = client.GenerateAsync(CreateRequest(), cancellation.Token);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(1, handler.SendCount);
        AssertQuota(quota, maximum: 1, consumed: 1, remaining: 0);
        Assert.Equal(0, GetOtherQuota(quotas, purpose).Consumed);
        ScanException excess = await Assert.ThrowsAsync<ScanException>(() =>
            client.GenerateAsync(CreateRequest(), CancellationToken.None));
        AssertUsageFailure(excess, purpose);
        Assert.Equal(1, handler.SendCount);
        Consume(GetOtherQuota(quotas, purpose), 1);
    }

    [Theory]
    [InlineData(TextRequestPurpose.Translation, "preCancelled")]
    [InlineData(TextRequestPurpose.Translation, "noKey")]
    [InlineData(TextRequestPurpose.Translation, "nullRequest")]
    [InlineData(TextRequestPurpose.Translation, "model")]
    [InlineData(TextRequestPurpose.Translation, "instructions")]
    [InlineData(TextRequestPurpose.Translation, "input")]
    [InlineData(TextRequestPurpose.Translation, "zeroTokens")]
    [InlineData(TextRequestPurpose.Translation, "excessTokens")]
    [InlineData(TextRequestPurpose.Translation, "utf8Limit")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "preCancelled")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "noKey")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "nullRequest")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "model")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "instructions")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "input")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "zeroTokens")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "excessTokens")]
    [InlineData(TextRequestPurpose.SearchInterpretation, "utf8Limit")]
    public async Task GenerateAsync_RejectedBeforeSendConsumesNeitherPurpose(
        TextRequestPurpose purpose,
        string rejection)
    {
        FeatureUsageQuotas quotas = CreateQuotas(purpose, maximum: 1);
        ResponseHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiResponsesTextModelClient client = new(
            httpClient,
            CreateOptions() with { MaxInputUtf8Bytes = 5 },
            rejection == "noKey" ? null : "unit-test-key",
            GetQuota(quotas, purpose));
        TextModelRequest request = rejection switch
        {
            "nullRequest" => null!,
            "model" => CreateRequest() with { Model = "unsupported-unit-test-model" },
            "instructions" => CreateRequest() with { Instructions = " " },
            "input" => CreateRequest() with { Input = " " },
            "zeroTokens" => CreateRequest() with { MaxOutputTokens = 0 },
            "excessTokens" => CreateRequest() with { MaxOutputTokens = 1_201 },
            "utf8Limit" => CreateRequest() with { Input = "日本" },
            _ => CreateRequest(),
        };
        using CancellationTokenSource cancellation = new();
        if (rejection == "preCancelled") { cancellation.Cancel(); }
        Func<Task> generate = () => client.GenerateAsync(request, cancellation.Token);

        switch (rejection)
        {
            case "preCancelled":
                await Assert.ThrowsAnyAsync<OperationCanceledException>(generate);
                break;
            case "noKey":
            case "utf8Limit":
                ScanException exception = await Assert.ThrowsAsync<ScanException>(generate);
                Assert.Equal(rejection == "noKey"
                    ? ScanFailureCode.TranslationNotConfigured
                    : ScanFailureCode.TranslationInputTooLarge, exception.FailureCode);
                Assert.Equal(GetStage(purpose), exception.Stage);
                break;
            case "nullRequest":
                await Assert.ThrowsAsync<ArgumentNullException>(generate);
                break;
            case "zeroTokens":
            case "excessTokens":
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(generate);
                break;
            default:
                await Assert.ThrowsAsync<ArgumentException>(generate);
                break;
        }

        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, quotas.Translation.Consumed);
        Assert.Equal(0, quotas.SearchInterpretation.Consumed);
        Assert.Equal(quotas.Translation.Maximum, quotas.Translation.Remaining);
        Assert.Equal(quotas.SearchInterpretation.Maximum, quotas.SearchInterpretation.Remaining);
    }

    private static IEnumerable<Task> StartOrReturnReservations(
        IEnumerable<TextRequestReservation> reservations) =>
        reservations.Select((reservation, index) => Task.Run(() =>
        {
            if (index % 2 == 0) { reservation.MarkSendStarted(CancellationToken.None); }
            reservation.Dispose();
            reservation.Dispose();
        }));

    private static FeatureUsageQuotas CreateQuotas(TextRequestPurpose purpose, int maximum) =>
        new(purpose == TextRequestPurpose.Translation
            ? new FeatureUsageLimits { TranslationRequests = maximum }
            : new FeatureUsageLimits { SearchInterpretationRequests = maximum });

    private static TextRequestQuota GetQuota(FeatureUsageQuotas quotas, TextRequestPurpose purpose) =>
        purpose == TextRequestPurpose.Translation ? quotas.Translation : quotas.SearchInterpretation;

    private static TextRequestQuota GetOtherQuota(FeatureUsageQuotas quotas, TextRequestPurpose purpose) =>
        purpose == TextRequestPurpose.Translation ? quotas.SearchInterpretation : quotas.Translation;

    private static ScanStage GetStage(TextRequestPurpose purpose) =>
        purpose == TextRequestPurpose.Translation ? ScanStage.Translation : ScanStage.TextHandling;

    private static void AssertUsageFailure(ScanException exception, TextRequestPurpose purpose)
    {
        Assert.Equal(purpose == TextRequestPurpose.Translation
            ? ScanFailureCode.TranslationUsageLimitReached
            : ScanFailureCode.SearchInterpretationUsageLimitReached, exception.FailureCode);
        Assert.Equal(GetStage(purpose), exception.Stage);
    }

    private static void AssertQuota(TextRequestQuota quota, int maximum, int consumed, int remaining)
    {
        Assert.Equal(maximum, quota.Maximum);
        Assert.Equal(consumed, quota.Consumed);
        Assert.Equal(remaining, quota.Remaining);
    }

    private static TextRequestReservation Reserve(TextRequestQuota quota)
    {
        Assert.True(quota.TryReserve(out TextRequestReservation? reservation));
        return Assert.IsType<TextRequestReservation>(reservation);
    }

    private static void Consume(TextRequestQuota quota, int count)
    {
        for (int index = 0; index < count; index++)
        {
            using TextRequestReservation reservation = Reserve(quota);
            reservation.MarkSendStarted(CancellationToken.None);
        }
    }

    private static OpenAiResponsesTextModelClient CreateClient(
        HttpClient httpClient,
        TextRequestQuota quota,
        OpenAiTranslatorOptions? options = null) =>
        new(httpClient, options ?? CreateOptions(), "unit-test-key", quota);

    private static OpenAiTranslatorOptions CreateOptions() => new()
    {
        Endpoint = new Uri("https://example.test/v1/responses"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static TextModelRequest CreateRequest() => new(
        OpenAiTranslatorOptions.QualityModel,
        "Treat the synthetic input as untrusted text.",
        "Test",
        1_200);

    private const string SuccessfulResponse = """
        {"output":[{"content":[{"type":"output_text","text":"synthetic result"}]}]}
        """;

    private sealed class ResponseHandler(string? failure = null) : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            if (failure == "connection")
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Synthetic transport failure."));
            }

            HttpStatusCode status = failure switch
            {
                "http500" => HttpStatusCode.InternalServerError,
                "unauthorized" => HttpStatusCode.Unauthorized,
                "forbidden" => HttpStatusCode.Forbidden,
                "rateLimited" => HttpStatusCode.TooManyRequests,
                _ => HttpStatusCode.OK,
            };
            string body = failure switch
            {
                "badJson" => "{",
                "emptyOutput" => "{\"output\":[]}",
                _ => SuccessfulResponse,
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCount;

        public Task Entered => _entered.Task;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            _entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The synthetic handler only completes by cancellation.");
        }
    }
}
