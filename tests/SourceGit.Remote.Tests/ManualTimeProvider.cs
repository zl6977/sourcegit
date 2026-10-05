internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly List<ManualTimer> _timers = new();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
    internal int EnabledTimers => _timers.Count(x => !x.Disposed && x.Due != long.MaxValue);
    internal Action[] CaptureScheduledCallbacks() => _timers.Where(x => !x.Disposed && x.Due != long.MaxValue)
        .Select<ManualTimer, Action>(timer => () => timer.Callback(timer.State)).ToArray();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer); timer.Change(dueTime, period); return timer;
    }
    internal void Advance(TimeSpan amount)
    {
        var target = _ticks + amount.Ticks;
        while (true)
        {
            var next = _timers.Where(x => !x.Disposed && x.Due <= target).MinBy(x => x.Due);
            if (next == null) break;
            _ticks = next.Due;
            next.Due = next.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + next.Period.Ticks;
            next.Callback(next.State);
        }
        _ticks = target;
    }
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        internal readonly TimerCallback Callback = callback;
        internal readonly object? State = state;
        internal long Due = long.MaxValue;
        internal TimeSpan Period;
        internal bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
            Period = period; return true;
        }
        public void Dispose() { Disposed = true; Due = long.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
