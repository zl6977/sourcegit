using SourceGit.Contracts;
using SourceGit.Core;

namespace SourceGit.Server;

// Short-lived browse sessions, scoped to a channel rather than a global path cache.
internal sealed class DirectorySnapshots : IDisposable
{
    private sealed class Snapshot(string owner, string path, DateTime expires)
    {
        public readonly string Owner = owner, Path = path;
        public readonly DateTime Expires = expires;
        public FileEntry[]? Entries;
        public long Bytes;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<Snapshot> _builders = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxSnapshots, _maxEntries;
    private readonly long _maxSnapshotBytes, _maxTotalBytes;
    private readonly Func<DateTime> _now;
    private readonly Func<string, IEnumerable<string>> _enumerate;
    private readonly ITimer _expiry;
    private bool _disposed;
    private long _bytes;
    internal int BuildCount { get; private set; }
    internal int ExpiryCallbacks { get; private set; }
    internal int RetainedCount { get { lock (_gate) return _snapshots.Count; } }
    internal int Count { get { lock (_gate) { Expire(); return _snapshots.Count; } } }
    internal long Bytes { get { lock (_gate) return _bytes; } }
    public DirectorySnapshots(TimeSpan? ttl = null, int maxSnapshots = 16, int maxEntries = 100000, long maxSnapshotBytes = 16 * 1024 * 1024, long maxTotalBytes = 64 * 1024 * 1024, Func<DateTime>? now = null, Func<string, IEnumerable<string>>? enumerate = null, TimeProvider? timeProvider = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(60); _maxSnapshots = maxSnapshots; _maxEntries = maxEntries;
        _maxSnapshotBytes = maxSnapshotBytes; _maxTotalBytes = maxTotalBytes;
        var clock = timeProvider ?? TimeProvider.System;
        var origin = clock.GetTimestamp();
        // Relative TTL is independent of wall-clock corrections. The legacy
        // injected clock remains supported by existing lifecycle checks.
        _now = now ?? (() => DateTime.UnixEpoch + clock.GetElapsedTime(origin));
        _enumerate = enumerate ?? Directory.EnumerateFileSystemEntries;
        _expiry = clock.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (_disposed) return;
                ExpiryCallbacks++;
                Expire(); // A late callback always rechecks the current deadline.
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public async Task<FileResult> ReadAsync(string owner, DirectoryRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.Offset < 0 || request.Count < 1) throw new ArgumentOutOfRangeException(nameof(request));
        var path = Path.GetFullPath(request.Path);
        var id = request.Cursor;
        Snapshot snapshot;
        if (id == null)
        {
            if (request.Offset != 0) throw new IOException("Directory paging requires a snapshot cursor; refresh the listing");
            id = Guid.NewGuid().ToString("N");
            snapshot = new(owner, path, _now() + _ttl);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this); Expire();
                if (_snapshots.Values.Count(x => x.Entries != null) + _builders.Count >= _maxSnapshots) throw new IOException("Directory snapshot capacity reached; close another listing or refresh later");
                _snapshots.Add(id, snapshot); _builders.Add(snapshot); BuildCount++;
                ScheduleExpiry();
            }
            try
            {
                var entries = await Task.Run(() =>
                {
                    var list = new List<FileEntry>();
                    long bytes = 0;
                    foreach (var entryPath in _enumerate(path))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var entry = RepositoryService.Entry(entryPath);
                        // Conservative budget includes entry object, array/list slots,
                        // string headers and UTF-16 strings. Building is also bounded.
                        bytes += 160L + 2L * (entry.Path.Length + entry.Name.Length);
                        if (list.Count >= _maxEntries || bytes > _maxSnapshotBytes) throw new IOException("Directory exceeds snapshot limit; choose a smaller directory");
                        list.Add(entry);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    list.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
                    cancellation.ThrowIfCancellationRequested();
                    return (Entries: list.ToArray(), Bytes: bytes);
                }, cancellation);
                lock (_gate)
                {
                    cancellation.ThrowIfCancellationRequested(); Expire();
                    if (!_snapshots.TryGetValue(id, out var current) || current != snapshot) throw Expired();
                    if (_bytes + entries.Bytes > _maxTotalBytes) throw new IOException("Directory snapshot memory capacity reached; close another listing or refresh later");
                    snapshot.Entries = entries.Entries; snapshot.Bytes = entries.Bytes; _bytes += entries.Bytes;
                }
            }
            catch { Release(owner, id); throw; }
            finally { lock (_gate) _builders.Remove(snapshot); }
        }
        lock (_gate)
        {
            Expire();
            if (!_snapshots.TryGetValue(id, out snapshot!) || snapshot.Owner != owner || snapshot.Path != path || snapshot.Entries == null) throw Expired();
            if (cancellation.IsCancellationRequested) { Remove(id); cancellation.ThrowIfCancellationRequested(); }
            if (request.Offset > snapshot.Entries.Length) throw new IOException("Invalid directory snapshot offset; refresh the listing");
            var count = Math.Min(Math.Clamp(request.Count, 1, 1000), snapshot.Entries.Length - request.Offset);
            var page = snapshot.Entries.AsSpan(request.Offset, count).ToArray();
            var next = request.Offset + count;
            var cursor = next < snapshot.Entries.Length ? id : null;
            if (cursor == null) Remove(id);
            return new(Entries: page, DirectoryCursor: cursor, NextOffset: next);
        }
    }
    private static IOException Expired() => new("Directory snapshot expired or belongs to another connection; refresh the listing");
    public void Release(string owner, string id)
    {
        lock (_gate) { if (_snapshots.TryGetValue(id, out var snapshot) && snapshot.Owner == owner) Remove(id); }
    }
    public void ReleaseOwner(string owner)
    {
        lock (_gate) foreach (var id in _snapshots.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray()) Remove(id);
    }
    private void Remove(string id)
    {
        if (_snapshots.Remove(id, out var snapshot)) _bytes -= snapshot.Bytes;
        ScheduleExpiry();
    }
    private void ScheduleExpiry()
    {
        if (_disposed) return;
        var due = _snapshots.Count == 0 ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromTicks(Math.Max(0, (_snapshots.Values.Min(x => x.Expires) - _now()).Ticks));
        _expiry.Change(due, Timeout.InfiniteTimeSpan);
    }
    private void Expire()
    {
        foreach (var id in _snapshots.Where(x => x.Value.Expires <= _now()).Select(x => x.Key).ToArray()) Remove(id);
        ScheduleExpiry();
    }
    public void Dispose() { lock (_gate) { _disposed = true; _snapshots.Clear(); _bytes = 0; } _expiry.Dispose(); }
}
