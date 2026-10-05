using System;
using System.Threading;

namespace SourceGit.Models
{
    // Owns one deadline. Conditions park overdue work until an explicit wake-up.
    internal sealed class FetchSchedule : IDisposable
    {
        private readonly object _gate = new();
        private readonly TimeProvider _clock;
        private readonly long _origin;
        private readonly ITimer _timer;
        private readonly Action<long> _dispatch;
        private TimeSpan _interval, _due;
        private long _generation;
        private bool _enabled, _eligible, _hasRemotes, _running, _queued, _disposed;
        private TimeSpan Now => _clock.GetElapsedTime(_origin);

        internal FetchSchedule(Action<long> dispatch, TimeProvider clock = null)
        {
            _dispatch = dispatch;
            _clock = clock ?? TimeProvider.System;
            _origin = _clock.GetTimestamp();
            _timer = _clock.CreateTimer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal void Configure(bool enabled, TimeSpan interval)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _enabled = enabled && interval > TimeSpan.Zero;
                _interval = interval > TimeSpan.Zero ? interval : TimeSpan.Zero;
                _due = Now + _interval;
                Invalidate(); Schedule();
            }
        }

        internal void ConditionsChanged(bool eligible, bool hasRemotes)
        {
            lock (_gate)
            {
                if (_disposed || (_eligible == eligible && _hasRemotes == hasRemotes)) return;
                _eligible = eligible; _hasRemotes = hasRemotes;
                Invalidate(); Schedule();
            }
        }

        internal bool TryStart(long generation)
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation || !_queued || !_enabled ||
                    !_eligible || !_hasRemotes || _running || Now < _due) return false;
                _queued = false; _running = true; Schedule(); return true;
            }
        }

        internal void MarkFetched()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _due = Now + _interval; Invalidate(); Schedule();
            }
        }

        internal void Completed()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _running = false; _due = Now + _interval; Invalidate(); Schedule();
            }
        }

        private void Invalidate() { _generation++; _queued = false; }
        private void Schedule()
        {
            var delay = Timeout.InfiniteTimeSpan;
            if (!_disposed && _enabled && _eligible && _hasRemotes && !_running && !_queued)
            {
                delay = TimeSpan.FromTicks(Math.Max(0, (_due - Now).Ticks));
                // ITimer has a maximum representable wait; long user periods retain their deadline.
                var maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
                if (delay > maximum) delay = maximum;
            }
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private void Tick(object state)
        {
            long generation;
            lock (_gate)
            {
                if (_disposed) return;
                if (!_enabled || !_eligible || !_hasRemotes || _running || _queued || Now < _due)
                { Schedule(); return; }
                _queued = true; generation = _generation; Schedule();
            }
            _dispatch(generation);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; Invalidate(); _timer.Dispose();
            }
        }
    }
}
