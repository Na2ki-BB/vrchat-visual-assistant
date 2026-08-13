namespace VrcVa.Windows.Osc;

internal sealed class OscTriggerGate
{
    private readonly TimeSpan _debounce;
    private bool _armed = true;
    private bool _lastObservedActiveWhileSettling;
    private TimeSpan? _lastTriggerAt;
    private TimeSpan? _settlingUntil;

    internal OscTriggerGate(TimeSpan debounce)
    {
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _debounce = debounce;
    }

    internal bool Observe(bool active, TimeSpan monotonicTimestamp)
    {
        if (_settlingUntil is TimeSpan settlingUntil)
        {
            if (monotonicTimestamp < settlingUntil)
            {
                _lastObservedActiveWhileSettling = active;
                return false;
            }

            _settlingUntil = null;
            _armed = !_lastObservedActiveWhileSettling;
        }

        if (!active)
        {
            _armed = true;
            return false;
        }

        if (!_armed)
        {
            return false;
        }

        _armed = false;
        if (_lastTriggerAt is not null && monotonicTimestamp - _lastTriggerAt < _debounce)
        {
            return false;
        }

        _lastTriggerAt = monotonicTimestamp;
        return true;
    }

    internal void ResetForAvatarChange(TimeSpan monotonicTimestamp)
    {
        _armed = false;
        _lastObservedActiveWhileSettling = false;
        _settlingUntil = monotonicTimestamp + _debounce;
    }
}
