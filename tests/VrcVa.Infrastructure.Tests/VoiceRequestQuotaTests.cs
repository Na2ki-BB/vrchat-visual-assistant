using System.Collections.Concurrent;
using VrcVa.Core;
using Reservation = VrcVa.Infrastructure.VoiceRequestQuota.VoiceRequestReservation;

namespace VrcVa.Infrastructure.Tests;

public sealed class VoiceRequestQuotaTests
{
    [Fact]
    public void DefaultBudgetsAreIndependentAudioSecondsAudioSendsTranslationAndInterpretation()
    {
        FeatureUsageQuotas quotas = new();
        Assert.Equal(300, quotas.Voice.MaximumSeconds);
        Assert.Equal(30, quotas.Voice.MaximumRequests);
        Consume(quotas.Voice, 32000);
        Assert.Equal(299, quotas.Voice.RemainingSeconds);
        Assert.Equal(29, quotas.Voice.RemainingRequests);
        Assert.Equal(10, quotas.Translation.Remaining);
        Assert.Equal(10, quotas.SearchInterpretation.Remaining);
        using TextRequestQuota.TextRequestReservation text = ReserveText(quotas.Translation);
        text.MarkSendStarted(default);
        using TextRequestQuota.TextRequestReservation query = ReserveText(quotas.SearchInterpretation);
        query.MarkSendStarted(default);
        Assert.Equal(299, quotas.Voice.RemainingSeconds);
        Assert.Equal(29, quotas.Voice.RemainingRequests);
        Assert.Equal(9, quotas.Translation.Remaining);
        Assert.Equal(9, quotas.SearchInterpretation.Remaining);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(3600)]
    public void SecondsBoundaryIsExactAndDoesNotSpendCountOnRejectedJointReservation(int maximum)
    {
        VoiceRequestQuota quota = new(maximum, maximumRequests: 300);
        int before = maximum - 1;
        while (before > 0)
        {
            int seconds = Math.Min(120, before);
            Consume(quota, seconds * 32000);
            before -= seconds;
        }
        Assert.Equal(1, quota.RemainingSeconds);
        int requests = quota.ConsumedRequests;
        Assert.False(quota.TryReserve(32002, out Reservation? rejected));
        Assert.Null(rejected);
        Assert.Equal(requests, quota.ConsumedRequests);
        Assert.Equal(1, quota.RemainingSeconds);
        Consume(quota, 32000);
        Assert.Equal(maximum, quota.ConsumedSeconds);
        Assert.Equal(0, quota.RemainingSeconds);
        Assert.True(quota.RemainingRequests > 0);
        Assert.False(quota.TryReserve(2, out _));
        Assert.Equal(requests + 1, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void SendCountBoundaryIsExactWithSecondsStillAvailable(int maximum)
    {
        VoiceRequestQuota quota = new(maximumSeconds: 3600, maximum);
        for (int index = 0; index < maximum; index++)
        {
            Assert.Equal(maximum - index, quota.RemainingRequests);
            Consume(quota, 2);
        }
        Assert.Equal(maximum, quota.ConsumedRequests);
        Assert.Equal(maximum, quota.ConsumedSeconds);
        Assert.Equal(0, quota.RemainingRequests);
        Assert.True(quota.RemainingSeconds > 0);
        Assert.False(quota.TryReserve(2, out _));
        Assert.Equal(maximum, quota.ConsumedSeconds);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(31998, 1)]
    [InlineData(32000, 1)]
    [InlineData(32002, 2)]
    [InlineData(64000, 2)]
    [InlineData(64002, 3)]
    [InlineData(3840000, 120)]
    public void EachTransmissionRoundsPcmSampleSecondsUpIndependently(int pcmBytes, int seconds)
    {
        VoiceRequestQuota quota = new();
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using Reservation reservation = Reserve(quota, pcmBytes);
            Assert.Equal(seconds, reservation.Seconds);
            reservation.MarkSendStarted(default);
            Assert.Equal(attempt * seconds, quota.ConsumedSeconds);
            Assert.Equal(attempt, quota.ConsumedRequests);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(32001)]
    [InlineData(3840002)]
    [InlineData(int.MaxValue)]
    public void InvalidPcmNeverReserves(int bytes)
    {
        VoiceRequestQuota quota = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => quota.TryReserve(bytes, out _));
        Assert.Equal(300, quota.RemainingSeconds);
        Assert.Equal(30, quota.RemainingRequests);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(3601, 30)]
    [InlineData(300, 0)]
    [InlineData(300, 301)]
    public void InvalidMaximumsRejectWholeConstruction(int seconds, int requests) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceRequestQuota(seconds, requests));

    [Fact]
    public void UnstartedDisposeReturnsBothOnceAndCannotStartLater()
    {
        VoiceRequestQuota quota = new(2, 1);
        Reservation reservation = Reserve(quota, 32002);
        Assert.Equal(0, quota.RemainingSeconds);
        Assert.Equal(0, quota.RemainingRequests);
        Assert.False(quota.TryReserve(2, out _));
        reservation.Dispose();
        reservation.Dispose();
        Assert.Equal(2, quota.RemainingSeconds);
        Assert.Equal(1, quota.RemainingRequests);
        Assert.Equal(0, quota.ConsumedSeconds);
        Assert.Throws<InvalidOperationException>(() => reservation.MarkSendStarted(default));
    }

    [Fact]
    public void PreCancelledReservationReturnsBothButStartedNeverReturnsConsumption()
    {
        VoiceRequestQuota quota = new(2, 1);
        Reservation cancelled = Reserve(quota, 32002);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cancelled.MarkSendStarted(cancellation.Token));
        cancelled.Dispose();
        Assert.Equal(2, quota.RemainingSeconds);
        Assert.Equal(1, quota.RemainingRequests);
        Reservation started = Reserve(quota, 32002);
        started.MarkSendStarted(default);
        Assert.Throws<InvalidOperationException>(() => started.MarkSendStarted(default));
        started.Dispose();
        started.Dispose();
        Assert.Equal(2, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
        Assert.Equal(0, quota.RemainingSeconds);
        Assert.Equal(0, quota.RemainingRequests);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotPartiallyReserveOrExceedEitherLimit()
    {
        VoiceRequestQuota quota = new(29, 30);
        ConcurrentBag<Reservation> admitted = [];
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            if (quota.TryReserve(32002, out Reservation? reservation)) { admitted.Add(reservation); }
        })));
        Assert.Equal(14, admitted.Count);
        Assert.Equal(1, quota.RemainingSeconds);
        Assert.Equal(16, quota.RemainingRequests);
        using Reservation finalSecond = Reserve(quota, 2);
        Assert.Equal(0, quota.RemainingSeconds);
        Assert.Equal(15, quota.RemainingRequests);
        await Task.WhenAll(admitted.Select((reservation, index) => Task.Run(() =>
        {
            if (index % 2 == 0) { reservation.MarkSendStarted(default); }
            reservation.Dispose();
            reservation.Dispose();
        })));
        finalSecond.MarkSendStarted(default);
        Assert.Equal(15, quota.ConsumedSeconds);
        Assert.Equal(8, quota.ConsumedRequests);
        Assert.Equal(14, quota.RemainingSeconds);
        Assert.Equal(22, quota.RemainingRequests);
    }

    [Fact]
    public async Task RacingStartAndDisposeHasOneAtomicOutcome()
    {
        for (int index = 0; index < 100; index++)
        {
            VoiceRequestQuota quota = new(2, 1);
            Reservation reservation = Reserve(quota, 32002);
            await Task.WhenAll(Task.Run(() =>
            {
                try { reservation.MarkSendStarted(default); }
                catch (InvalidOperationException) { }
            }), Task.Run(reservation.Dispose));
            Assert.True(quota.ConsumedRequests is 0 or 1);
            Assert.Equal(quota.ConsumedRequests * 2, quota.ConsumedSeconds);
            Assert.Equal(2 - quota.ConsumedSeconds, quota.RemainingSeconds);
            Assert.Equal(1 - quota.ConsumedRequests, quota.RemainingRequests);
        }
    }

    [Fact]
    public void ApplyLimitsPreservesVoiceIdentityConsumptionAndPendingStartSnapshot()
    {
        FeatureUsageQuotas quotas = new(new() { VoiceSeconds = 10, VoiceRequests = 5 });
        VoiceRequestQuota original = quotas.Voice;
        Consume(original, 32002);
        using Reservation pending = Reserve(original, 64002);
        quotas.ApplyLimits(quotas.Limits with { VoiceSeconds = 1, VoiceRequests = 1 });
        Assert.Same(original, quotas.Voice);
        Assert.Equal(2, original.ConsumedSeconds);
        Assert.Equal(1, original.ConsumedRequests);
        Assert.Equal(0, original.RemainingSeconds);
        Assert.Equal(0, original.RemainingRequests);
        Assert.False(original.TryReserve(2, out _));
        pending.MarkSendStarted(default);
        Assert.Equal(5, original.ConsumedSeconds);
        Assert.Equal(2, original.ConsumedRequests);
        quotas.ApplyLimits(quotas.Limits with { VoiceSeconds = 7, VoiceRequests = 4 });
        Assert.Equal(2, original.RemainingSeconds);
        Assert.Equal(2, original.RemainingRequests);
        Consume(original, 32002);
        Assert.Equal(7, original.ConsumedSeconds);
        Assert.Equal(3, original.ConsumedRequests);
        Assert.Equal(0, original.RemainingSeconds);
        Assert.Equal(1, original.RemainingRequests);
    }

    [Fact]
    public void InvalidReloadDoesNotChangeAnyQuotaOrReleaseReservations()
    {
        FeatureUsageQuotas quotas = new();
        FeatureUsageLimits original = quotas.Limits;
        Consume(quotas.Voice, 32002);
        using Reservation pending = Reserve(quotas.Voice, 64002);
        foreach (FeatureUsageLimits invalid in new[]
        {
            original with { VoiceSeconds = 0 }, original with { VoiceRequests = 301 },
            original with { TranslationRequests = 0 }, original with { SearchInterpretationRequests = 101 },
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => quotas.ApplyLimits(invalid));
            Assert.Same(original, quotas.Limits);
            Assert.Equal(300, quotas.Voice.MaximumSeconds);
            Assert.Equal(30, quotas.Voice.MaximumRequests);
            Assert.Equal(295, quotas.Voice.RemainingSeconds);
            Assert.Equal(28, quotas.Voice.RemainingRequests);
            Assert.Equal(10, quotas.Translation.Maximum);
            Assert.Equal(10, quotas.SearchInterpretation.Maximum);
        }
        pending.Dispose();
        Assert.Equal(298, quotas.Voice.RemainingSeconds);
        Assert.Equal(29, quotas.Voice.RemainingRequests);
    }

    private static Reservation Reserve(VoiceRequestQuota quota, int pcmBytes)
    {
        Assert.True(quota.TryReserve(pcmBytes, out Reservation? reservation));
        return Assert.IsType<Reservation>(reservation);
    }
    private static void Consume(VoiceRequestQuota quota, int pcmBytes)
    {
        using Reservation reservation = Reserve(quota, pcmBytes);
        reservation.MarkSendStarted(default);
    }
    private static TextRequestQuota.TextRequestReservation ReserveText(TextRequestQuota quota)
    {
        Assert.True(quota.TryReserve(out TextRequestQuota.TextRequestReservation? reservation));
        return Assert.IsType<TextRequestQuota.TextRequestReservation>(reservation);
    }
}
