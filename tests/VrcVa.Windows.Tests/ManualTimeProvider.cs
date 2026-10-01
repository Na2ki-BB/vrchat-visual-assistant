namespace VrcVa.Windows.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) { return _timestamp; } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (_sync) { _timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        List<ManualTimer> due;
        lock (_sync)
        {
            _timestamp += amount.Ticks;
            due = _timers.Where(x => !x.Disposed && x.Due <= _timestamp).ToList();
            foreach (ManualTimer timer in due) { timer.Due = long.MaxValue; }
        }
        foreach (ManualTimer timer in due) { timer.Callback(timer.State); }
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (Disposed) { return false; }
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._timestamp + dueTime.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner._sync) { Disposed = true; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
