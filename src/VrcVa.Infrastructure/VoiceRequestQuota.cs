using System.Diagnostics.CodeAnalysis;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>
/// App-owned process-lifetime audio seconds AND attempts. A single atomic lease
/// reserves both; only an unstarted send can return them. No client owns/reset API.
/// </summary>
public sealed class VoiceRequestQuota
{
    private readonly object _sync;
    private int _maximumSeconds;
    private int _maximumRequests;
    private int _consumedSeconds;
    private int _consumedRequests;
    private int _reservedSeconds;
    private int _reservedRequests;

    public VoiceRequestQuota(
        int maximumSeconds = FeatureUsageLimits.DefaultVoiceSeconds,
        int maximumRequests = FeatureUsageLimits.DefaultVoiceRequests)
        : this(maximumSeconds, maximumRequests, new object())
    {
    }

    internal VoiceRequestQuota(int maximumSeconds, int maximumRequests, object sync)
    {
        ValidateMaximums(maximumSeconds, maximumRequests);
        _sync = sync;
        _maximumSeconds = maximumSeconds;
        _maximumRequests = maximumRequests;
    }

    public int MaximumSeconds { get { lock (_sync) { return _maximumSeconds; } } }
    public int MaximumRequests { get { lock (_sync) { return _maximumRequests; } } }
    public int ConsumedSeconds { get { lock (_sync) { return _consumedSeconds; } } }
    public int ConsumedRequests { get { lock (_sync) { return _consumedRequests; } } }
    public int RemainingSeconds { get { lock (_sync) { return Math.Max(0, _maximumSeconds - _consumedSeconds - _reservedSeconds); } } }
    public int RemainingRequests { get { lock (_sync) { return Math.Max(0, _maximumRequests - _consumedRequests - _reservedRequests); } } }

    public bool TryReserve(int pcmDataBytes, [NotNullWhen(true)] out VoiceRequestReservation? reservation)
    {
        int seconds = VoiceAudioFormat.GetQuotaSeconds(pcmDataBytes);
        lock (_sync)
        {
            reservation = null;
            if (seconds > _maximumSeconds - _consumedSeconds - _reservedSeconds
                || _consumedRequests + _reservedRequests >= _maximumRequests)
            {
                return false;
            }

            _reservedSeconds += seconds;
            _reservedRequests++;
            reservation = new VoiceRequestReservation(this, seconds);
            return true;
        }
    }

    internal void UpdateMaximums(int seconds, int requests)
    {
        ValidateMaximums(seconds, requests);
        lock (_sync) { _maximumSeconds = seconds; _maximumRequests = requests; }
    }

    private static void ValidateMaximums(int seconds, int requests)
    {
        if (seconds is < 1 or > FeatureUsageLimits.MaximumAllowedVoiceSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        if (requests is < 1 or > FeatureUsageLimits.MaximumAllowedVoiceRequests)
        {
            throw new ArgumentOutOfRangeException(nameof(requests));
        }
    }

    public sealed class VoiceRequestReservation : IDisposable
    {
        private readonly VoiceRequestQuota _owner;
        private bool _started;
        private bool _disposed;

        internal VoiceRequestReservation(VoiceRequestQuota owner, int seconds)
        {
            _owner = owner;
            Seconds = seconds;
        }

        public int Seconds { get; }

        /// <summary>Commit immediately before attempting SendAsync, even if it fails.</summary>
        public void MarkSendStarted(CancellationToken cancellationToken)
        {
            lock (_owner._sync)
            {
                if (_disposed || _started)
                {
                    throw new InvalidOperationException("An audio reservation may start only once.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                _owner._reservedSeconds -= Seconds;
                _owner._reservedRequests--;
                _owner._consumedSeconds += Seconds;
                _owner._consumedRequests++;
                _started = true;
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) { return; }
                if (!_started)
                {
                    _owner._reservedSeconds -= Seconds;
                    _owner._reservedRequests--;
                }
                _disposed = true;
            }
        }
    }
}
