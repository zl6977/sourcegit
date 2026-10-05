namespace SourceGit.Core;

// One timer per repository; idle repositories have no scheduled callbacks.
internal sealed class RepositoryChangeDebounce : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private readonly Action _notify;
    private bool _disposed, _dirty, _running;
    private long _first, _last;
    private TimeSpan _delay;
    private long _failures;
    internal Action<string>? Diagnostic { get; set; }
    private void Report(string detail)
    {
        var message = $"REPOSITORY-NOTIFY {_clock.GetUtcNow():O} {detail}";
        try { Console.Error.WriteLine(message); Diagnostic?.Invoke(message); }
        catch (Exception) { } // Consumer diagnostics cannot lose dirty work.
    }
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Maximum = TimeSpan.FromSeconds(2);

    internal RepositoryChangeDebounce(Action notify, TimeProvider? clock = null)
    {
        _notify = notify;
        _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Schedule(bool urgent = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock.GetTimestamp();
            var alreadyUrgent = _dirty && _delay == TimeSpan.Zero;
            if (!_dirty) _first = now;
            _dirty = true;
            _last = now;
            _delay = urgent || alreadyUrgent ? TimeSpan.Zero : Settle;
            Arm();
        }
    }

    private TimeSpan Remaining()
    {
        var now = _clock.GetTimestamp();
        var settle = _delay - _clock.GetElapsedTime(_last, now);
        var maximum = Maximum - _clock.GetElapsedTime(_first, now);
        return TimeSpan.FromTicks(Math.Max(0, Math.Min(settle.Ticks, maximum.Ticks)));
    }

    private void Arm()
    {
        if (!_disposed && _dirty && !_running)
            _timer.Change(Remaining(), Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || !_dirty || _running) return;
            if (Remaining() > TimeSpan.Zero) { Arm(); return; }
            _dirty = false;
            _running = true;
        }
        var succeeded = false;
        try { _notify(); succeeded = true; }
        catch (Exception error)
        {
            // A consumer failure must not escape the ThreadPool timer or lose
            // the refresh. Retry only failed work, with a bounded delay.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _dirty = true;
                    _first = _last = _clock.GetTimestamp();
                    _delay = TimeSpan.FromSeconds(1);
                    _failures++;
                    if ((_failures & (_failures - 1)) == 0)
                        Report($"refresh-retry reason={error.GetType().Name} attempt={_failures} nextIn=1s");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (succeeded && _failures != 0) { Report($"refresh-recovered attempts={_failures}"); _failures = 0; }
                _running = false;
                Arm();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _dirty = false;
            _timer.Dispose();
        }
        // An admitted callback may finish; never join a consumer while holding
        // its repository/session lock (including reentrant Close callbacks).
    }
}
