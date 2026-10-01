using System.Diagnostics.CodeAnalysis;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

public enum TextRequestPurpose
{
    Translation,
    SearchInterpretation,
}

/// <summary>
/// One purpose's process-lifetime request attempts. Client replacement never owns
/// this counter. A reservation can be returned only before SendAsync is attempted.
/// </summary>
public sealed class TextRequestQuota
{
    public const int HardMaximum = FeatureUsageLimits.MaximumAllowedTextRequests;

    private readonly object _sync;
    private int _maximum;
    private int _consumed;
    private int _reserved;

    public TextRequestQuota(
        int maximum = FeatureUsageLimits.DefaultTextRequests,
        TextRequestPurpose purpose = TextRequestPurpose.Translation)
        : this(maximum, purpose, new object())
    {
    }

    internal TextRequestQuota(int maximum, TextRequestPurpose purpose, object sync)
    {
        ValidateMaximum(maximum);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        _sync = sync;
        _maximum = maximum;
        Purpose = purpose;
    }

    public TextRequestPurpose Purpose { get; }

    public int Maximum { get { lock (_sync) { return _maximum; } } }

    public int Consumed { get { lock (_sync) { return _consumed; } } }

    public int Remaining { get { lock (_sync) { return Math.Max(0, _maximum - _consumed - _reserved); } } }

    public bool TryReserve([NotNullWhen(true)] out TextRequestReservation? reservation)
    {
        lock (_sync)
        {
            reservation = null;
            if (_consumed + _reserved >= _maximum) { return false; }
            _reserved++;
            reservation = new TextRequestReservation(this);
            return true;
        }
    }

    internal void UpdateMaximum(int maximum)
    {
        ValidateMaximum(maximum);
        lock (_sync) { _maximum = maximum; }
    }

    private static void ValidateMaximum(int maximum)
    {
        if (maximum is < 1 or > HardMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum,
                $"Text request maximum must be between 1 and {HardMaximum}.");
        }
    }

    public sealed class TextRequestReservation : IDisposable
    {
        private readonly TextRequestQuota _owner;
        private bool _started;
        private bool _disposed;

        internal TextRequestReservation(TextRequestQuota owner) => _owner = owner;

        /// <summary>
        /// Commit immediately before invoking SendAsync. Cancellation observed here
        /// returns the reservation on disposal; cancellation afterwards still counts.
        /// </summary>
        public void MarkSendStarted(CancellationToken cancellationToken)
        {
            lock (_owner._sync)
            {
                if (_disposed || _started)
                {
                    throw new InvalidOperationException("A request reservation may start only once.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                _owner._reserved--;
                _owner._consumed++;
                _started = true;
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) { return; }
                if (!_started) { _owner._reserved--; }
                _disposed = true;
            }
        }
    }
}
