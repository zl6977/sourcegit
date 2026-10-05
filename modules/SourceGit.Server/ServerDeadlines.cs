namespace SourceGit.Server;

// The shared owner gate serializes deadlines with handshake/ownership changes.
// Expired channels remain registered until their actual disconnect is observed.
internal sealed class ServerDeadlines<T> : IDisposable where T : notnull
{
    private sealed class Channel(TimeSpan deadline, Action close)
    {
        internal TimeSpan Deadline = deadline;
        internal readonly Action Close = close;
        internal bool Expired;
        internal bool RetryClose;
        internal TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
        internal long CloseFailures;
    }
    private readonly object _gate;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private readonly TimeSpan _idleGrace, _channelGrace;
    private readonly Func<bool> _hasActive, _tryStop;
    private readonly Dictionary<T, Channel> _channels = new();
    private readonly ITimer _timer;
    private TimeSpan _idleDeadline;
    private bool _started, _stopped;
    internal int Callbacks { get; private set; }
    internal Action<string>? Diagnostic { get; set; }
    private void Report(string detail)
    {
        var message = $"CHANNEL-DEADLINE {_clock.GetUtcNow():O} elapsed={Now.TotalSeconds:F3}s {detail}";
        try
        {
            Console.Error.WriteLine(message);
            Diagnostic?.Invoke(message);
        }
        catch (Exception) { } // Logging must not prevent retry/ownership cleanup.
    }
    private TimeSpan Now => _clock.GetElapsedTime(_origin);
    internal ServerDeadlines(object ownerGate, TimeSpan idleGrace, TimeSpan channelGrace,
        Func<bool> hasActive, Func<bool> tryStop, TimeProvider? clock = null)
    {
        _gate = ownerGate; _idleGrace = idleGrace; _channelGrace = channelGrace;
        _hasActive = hasActive; _tryStop = tryStop;
        _clock = clock ?? TimeProvider.System; _origin = _clock.GetTimestamp();
        _timer = _clock.CreateTimer(OnDeadline, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    internal void Start()
    {
        lock (_gate) { if (_stopped) return; _started = true; _idleDeadline = Now + _idleGrace; Schedule(); }
    }
    internal void Add(T key, Action close)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _channels.Add(key, new Channel(Now + _channelGrace, close));
            Schedule();
        }
    }
    internal void Touch(T key)
    {
        lock (_gate)
        {
            if (_stopped || !_channels.TryGetValue(key, out var channel) || channel.Expired) return;
            channel.Deadline = Now + _channelGrace;
            Schedule();
        }
    }
    internal void Remove(T key)
    {
        lock (_gate)
        {
            if (_stopped || !_channels.Remove(key)) return;
            if (_channels.Count == 0) _idleDeadline = Now + _idleGrace;
            Schedule();
        }
    }
    internal void OperationChanged() { lock (_gate) Schedule(); }
    private void Schedule()
    {
        if (!_started || _stopped) return;
        var due = TimeSpan.MaxValue;
        foreach (var channel in _channels.Values)
            if ((!channel.Expired || channel.RetryClose) && channel.Deadline < due) due = channel.Deadline;
        if (_channels.Count == 0 && !_hasActive()) due = _idleDeadline;
        // .NET timers have a maximum delay. Far deadlines use a bounded chunk,
        // without turning the ordinary seconds/minutes policy into periodic scans.
        var wait = due == TimeSpan.MaxValue ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromTicks(Math.Min(4294967294L * TimeSpan.TicksPerMillisecond, Math.Max(0, (due - Now).Ticks)));
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }
    private void OnDeadline(object? state)
    {
        List<(T Key, Channel Channel)> close = new();
        lock (_gate)
        {
            if (_stopped) return;
            Callbacks++;
            var now = Now;
            foreach (var (key, channel) in _channels)
            {
                if (channel.Expired && !channel.RetryClose || channel.Deadline > now) continue;
                channel.Expired = true;
                channel.RetryClose = false;
                close.Add((key, channel));
            }
            if (_channels.Count == 0 && _idleDeadline <= now && !_hasActive())
            {
                // The callback closes admission atomically with the active check.
                // A concurrent operation can make it refuse; completion wakes us.
                if (_tryStop()) { _stopped = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
                else { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
            }
            else Schedule();
        }
        foreach (var (key, channel) in close)
        {
            try
            {
                channel.Close();
                if (channel.CloseFailures != 0) Report($"close-recovered attempts={channel.CloseFailures}");
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (_stopped || !_channels.TryGetValue(key, out var current) || current != channel) continue;
                    channel.RetryClose = true;
                    channel.Deadline = Now + channel.RetryDelay;
                    channel.CloseFailures++;
                    if ((channel.CloseFailures & (channel.CloseFailures - 1)) == 0)
                        Report($"close-retry reason={error.GetType().Name} attempt={channel.CloseFailures} nextIn={channel.RetryDelay.TotalSeconds:F3}s");
                    channel.RetryDelay = TimeSpan.FromMilliseconds(Math.Min(5000, channel.RetryDelay.TotalMilliseconds * 2));
                    Schedule(); // Only a real close error retries; success awaits actual disconnect.
                }
            }
        }
    }
    public void Dispose() { lock (_gate) { _stopped = true; _channels.Clear(); } _timer.Dispose(); }
}
