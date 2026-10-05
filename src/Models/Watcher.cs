using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class Watcher : IDisposable
    {
        public class LockContext : IDisposable
        {
            public LockContext(Watcher target)
            {
                _target = target;
                lock (_target._gate)
                {
                    ObjectDisposedException.ThrowIf(_target._disposed, _target);
                    _target._lockCount++;
                    _target.Schedule();
                }
            }

            public void Dispose()
            {
                var target = Interlocked.Exchange(ref _target, null);
                if (target != null) lock (target._gate) { if (target._lockCount > 0) target._lockCount--; target.Schedule(); }
            }

            private Watcher _target;
        }

        public Watcher(IRepository repo, string fullpath, string gitDir) : this(repo, fullpath, gitDir, null)
        {
        }

        internal Watcher(IRepository repo, string fullpath, string gitDir, Action<FileSystemWatcher> sourceCreated) : this(repo, TimeProvider.System)
        {
            _root = new SourceGit.Remote.RepositoryDirectoryInfo(fullpath).FullName;

            if (SourceGit.Remote.RepositoryPath.IsRemote(fullpath))
            {
                _remoteSubscription = SourceGit.Remote.RemoteWorkspace.ForPath(fullpath).Subscribe(fullpath, () =>
                {
                    NotifyRepositoryChanged();
                });
                return;
            }

            try
            {
                var testGitDir = new SourceGit.Remote.RepositoryDirectoryInfo(SourceGit.Remote.RepositoryPath.Combine(fullpath, ".git")).FullName;
                var desiredDir = new SourceGit.Remote.RepositoryDirectoryInfo(gitDir).FullName;
                if (testGitDir.Equals(desiredDir, StringComparison.Ordinal))
                {
                    var combined = new FileSystemWatcher();
                    _watchers.Add(combined);
                    sourceCreated?.Invoke(combined);
                    combined.Path = fullpath;
                    combined.Filter = "*";
                    combined.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                    combined.IncludeSubdirectories = true;
                    combined.Created += OnRepositoryChanged;
                    combined.Renamed += OnRepositoryChanged;
                    combined.Changed += OnRepositoryChanged;
                    combined.Deleted += OnRepositoryChanged;
                    combined.Error += OnWatcherError;
                    combined.EnableRaisingEvents = false;

                }
                else
                {
                    var wc = new FileSystemWatcher();
                    _watchers.Add(wc);
                    sourceCreated?.Invoke(wc);
                    wc.Path = fullpath;
                    wc.Filter = "*";
                    wc.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                    wc.IncludeSubdirectories = true;
                    wc.Created += OnWorkingCopyChanged;
                    wc.Renamed += OnWorkingCopyChanged;
                    wc.Changed += OnWorkingCopyChanged;
                    wc.Deleted += OnWorkingCopyChanged;
                    wc.Error += OnWatcherError;
                    wc.EnableRaisingEvents = false;

                    var git = new FileSystemWatcher();
                    _watchers.Add(git);
                    sourceCreated?.Invoke(git);
                    git.Path = gitDir;
                    git.Filter = "*";
                    git.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                    git.IncludeSubdirectories = true;
                    git.Created += OnGitDirChanged;
                    git.Renamed += OnGitDirChanged;
                    git.Changed += OnGitDirChanged;
                    git.Deleted += OnGitDirChanged;
                    git.Error += OnWatcherError;
                    git.EnableRaisingEvents = false;

                }
            }
            catch (System.Exception error)
            {
                foreach (var watcher in _watchers)
                    watcher.Dispose();
                _watchers.Clear();
                // The path cannot be watched at all (e.g. //wsl.localhost UNC shares reject
                // ReadDirectoryChangesW): only degraded refresh has a recurring deadline.
                SetDegraded(reason: "initialization-" + error.GetType().Name);
            }

            // Starts filesystem watchers in another thread to avoid UI blocking
            var sources = _watchers.ToArray();
            Task.Run(() =>
            {
                try
                {
                    foreach (var watcher in sources)
                    {
                        if (_disposed) return;
                        try { watcher.EnableRaisingEvents = true; SignalConditionsChanged(); }
                        catch (Exception error) { SetDegraded(watcher, "enable-" + error.GetType().Name); }
                    }
                }
                catch (System.Exception error)
                {
                    // EnableRaisingEvents can fail outright on paths that reject ReadDirectoryChangesW
                    // (e.g. //wsl.localhost UNC shares); fall back to polling. Also occurs while `Dispose` runs.
                    SetDegraded(reason: "enable-" + error.GetType().Name);
                }
            });
        }

        [Flags]
        internal enum Dirty { Branch = 1, WorkingCopy = 2, Tags = 4, Stashes = 8, Submodules = 16, All = 31 }
        private sealed class RefreshDeferredException : Exception { }
        private readonly object _gate = new();
        private readonly TimeProvider _clock;
        private readonly long _origin;
        private readonly ITimer _timer;
        private readonly IRepository _repo;
        private readonly string _root;
        private readonly List<FileSystemWatcher> _watchers;
        private readonly Dictionary<Dirty, (TimeSpan First, TimeSpan Due)> _pending = new();
        private bool _disposed, _running, _degraded, _unwatchable;
        private readonly HashSet<FileSystemWatcher> _failedSources = new();
        private int _lockCount;
        private TimeSpan _nextFallback;
        private IDisposable _remoteSubscription;
        private int _pollIntervalSeconds = 30;
        internal int TimerCallbacks { get; private set; }
        internal Action<string> Diagnostic { get; set; }
        private long _fallbacks, _refreshFailures;
        private void Report(string detail)
        {
            var message = $"WATCHER {DateTimeOffset.UtcNow:O} elapsed={Now.TotalSeconds:F3}s {detail}";
            try
            {
                System.Diagnostics.Trace.WriteLine(message);
                Diagnostic?.Invoke(message);
            }
            catch (Exception) { } // Diagnostics cannot break refresh ownership.
        }
        private static bool ReportCount(long count) => (count & (count - 1)) == 0;
        internal event Action ConditionsChanged;
        private void SignalConditionsChanged()
        {
            lock (_gate) { if (!_disposed) ConditionsChanged?.Invoke(); }
        }
        private void SignalLockChanged(FileSystemEventArgs e)
        {
            if (e.FullPath.EndsWith(".lock", StringComparison.Ordinal) ||
                e is RenamedEventArgs renamed && renamed.OldFullPath.EndsWith(".lock", StringComparison.Ordinal))
                SignalConditionsChanged();
        }
        private TimeSpan Now => _clock.GetElapsedTime(_origin);
        private static readonly Dirty[] Categories = [Dirty.Branch, Dirty.WorkingCopy, Dirty.Tags, Dirty.Stashes, Dirty.Submodules];
        internal Watcher(IRepository repo, TimeProvider clock)
        {
            _repo = repo; _root = string.Empty; _watchers = new List<FileSystemWatcher>();
            _clock = clock; _origin = clock.GetTimestamp();
            _timer = clock.CreateTimer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        public IDisposable Lock() => new LockContext(this);
        public void MarkBranchUpdated() => Clear(Dirty.Branch | Dirty.WorkingCopy);
        public void MarkTagUpdated() => Clear(Dirty.Tags);
        public void MarkWorkingCopyUpdated() => Clear(Dirty.WorkingCopy);
        public void MarkStashUpdated() => Clear(Dirty.Stashes);
        public void MarkSubmodulesUpdated() => Clear(Dirty.Submodules);
        private void Clear(Dirty flags)
        {
            lock (_gate)
            {
                foreach (var category in Categories) if ((flags & category) != 0) _pending.Remove(category);
                Schedule();
            }
        }
        internal void NotifyRepositoryChanged() => MarkDirty(Dirty.All, TimeSpan.FromMilliseconds(100));
        internal void MarkDirty(Dirty flags, TimeSpan delay)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var now = Now;
                foreach (var category in Categories)
                {
                    if ((flags & category) == 0) continue;
                    var first = _pending.TryGetValue(category, out var previous) ? previous.First : now;
                    var latest = now + delay;
                    var maximum = first + TimeSpan.FromSeconds(2);
                    _pending[category] = (first, latest < maximum ? latest : maximum);
                }
                Schedule();
            }
        }
        public int PollIntervalSeconds
        {
            get { lock (_gate) return _pollIntervalSeconds; }
            set { lock (_gate) { _pollIntervalSeconds = Math.Max(1, value); _nextFallback = Now + TimeSpan.FromSeconds(_pollIntervalSeconds); Schedule(); } }
        }
        internal void SetDegraded(FileSystemWatcher source = null, string reason = "source-failure")
        {
            lock (_gate)
            {
                if (_disposed) return;
                var changed = source == null ? !_unwatchable : _failedSources.Add(source);
                if (source == null) _unwatchable = true;
                if (!_degraded) { _nextFallback = Now + TimeSpan.FromSeconds(_pollIntervalSeconds); _fallbacks = 0; }
                _degraded = true;
                if (changed) Report($"degraded-enter reason={reason} failedSources={_failedSources.Count} unwatchable={_unwatchable} nextIn={Math.Max(0, (_nextFallback - Now).TotalSeconds):F3}s");
                Schedule();
            }
        }
        private void OnWatcherError(object sender, ErrorEventArgs error)
        {
            SetDegraded(sender as FileSystemWatcher, "notification-" + error.GetException().GetType().Name);
            NotifyRepositoryChanged(); // Overflow can have lost categories.
            SignalConditionsChanged();
        }
        internal void RecoverSource(object sender)
        {
            lock (_gate)
            {
                if (_disposed || sender is not FileSystemWatcher source || !_failedSources.Remove(source)) return;
                _degraded = _unwatchable || _failedSources.Count != 0;
                Report($"source-recovered degraded={_degraded} failedSources={_failedSources.Count} fallbackCount={_fallbacks}");
                ConditionsChanged?.Invoke();
                Schedule();
            }
        }
        private void Schedule()
        {
            if (_disposed) return;
            if (_running || _lockCount > 0) { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
            var due = _pending.Count == 0 ? TimeSpan.MaxValue : _pending.Values.Min(x => x.Due);
            if (_degraded && _nextFallback < due) due = _nextFallback;
            _timer.Change(due == TimeSpan.MaxValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(Math.Max(0, (due - Now).Ticks)), Timeout.InfiniteTimeSpan);
        }
        private void Tick(object sender)
        {
            Dirty batch = 0;
            bool fallback = false;
            lock (_gate)
            {
                if (_disposed || _running) return;
                TimerCallbacks++;
                if (_lockCount > 0) { Schedule(); return; }
                var now = Now;
                if (_degraded && _nextFallback <= now)
                {
                    _nextFallback = now + TimeSpan.FromSeconds(_pollIntervalSeconds);
                    batch = Dirty.All; // Preserve not-yet-due settling events for their later batch.
                    fallback = true;
                    _fallbacks++;
                    if (ReportCount(_fallbacks)) Report($"degraded-refresh count={_fallbacks} nextIn={_pollIntervalSeconds}s");
                }
                foreach (var category in Categories)
                    if (_pending.TryGetValue(category, out var pending) && pending.Due <= now)
                    { batch |= category; _pending.Remove(category); }
                if (batch == 0) { Schedule(); return; }
                _running = true;
            }
            var succeeded = false;
            try
            {
                if (fallback) SignalConditionsChanged();
                var branches = (batch & Dirty.Branch) != 0;
                if (branches) { Invoke(_repo.RefreshBranches); Invoke(_repo.RefreshWorktrees); }
                if (branches || (batch & Dirty.WorkingCopy) != 0) Invoke(_repo.RefreshWorkingCopyChanges);
                if ((batch & Dirty.Submodules) != 0 || branches && _repo.MayHaveSubmodules()) Invoke(_repo.RefreshSubmodules);
                if ((batch & Dirty.Stashes) != 0) Invoke(_repo.RefreshStashes);
                if ((batch & Dirty.Tags) != 0) Invoke(_repo.RefreshTags);
                if (branches || (batch & Dirty.Tags) != 0) Invoke(_repo.RefreshCommits);
                succeeded = true;
            }
            catch (RefreshDeferredException) { MarkDirty(batch, TimeSpan.Zero); }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        var now = Now;
                        _refreshFailures++;
                        if (ReportCount(_refreshFailures)) Report($"refresh-retry reason={error.GetType().Name} attempt={_refreshFailures} nextIn=1s");
                        foreach (var category in Categories)
                            if ((batch & category) != 0) _pending[category] = (now, now + TimeSpan.FromSeconds(1));
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (succeeded && _refreshFailures != 0)
                    {
                        Report($"refresh-recovered attempts={_refreshFailures}");
                        _refreshFailures = 0;
                    }
                    _running = false;
                    Schedule();
                }
            }
        }
        private void Invoke(Action refresh)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_lockCount > 0) throw new RefreshDeferredException();
                // Repository callbacks schedule their own asynchronous Git/UI work.
                refresh();
            }
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _pending.Clear(); }
            _timer.Dispose(); _remoteSubscription?.Dispose();
            foreach (var watcher in _watchers) watcher.Dispose();
        }
        private void OnRepositoryChanged(object o, FileSystemEventArgs e)
        {
            RecoverSource(o);
            SignalLockChanged(e);
            if (string.IsNullOrEmpty(e.Name) || e.Name.Equals(".git", StringComparison.Ordinal))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            if (name.StartsWith(".git/", StringComparison.Ordinal))
                HandleGitDirFileChanged(name.Substring(5));
            else
                HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        private void OnGitDirChanged(object o, FileSystemEventArgs e)
        {
            RecoverSource(o);
            SignalLockChanged(e);
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            HandleGitDirFileChanged(name);
        }

        private void OnWorkingCopyChanged(object o, FileSystemEventArgs e)
        {
            RecoverSource(o);
            SignalLockChanged(e);
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.Equals(".git", StringComparison.Ordinal) ||
                name.StartsWith(".git/", StringComparison.Ordinal) ||
                name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        private void HandleGitDirFileChanged(string name)
        {
            if (name.Contains("fsmonitor--daemon/", StringComparison.Ordinal) ||
                name.EndsWith(".lock", StringComparison.Ordinal) ||
                name.StartsWith("lfs/", StringComparison.Ordinal))
                return;

            if (name.StartsWith("modules", StringComparison.Ordinal))
            {
                if (name.EndsWith("/HEAD", StringComparison.Ordinal) ||
                    name.EndsWith("/ORIG_HEAD", StringComparison.Ordinal))
                {
                    var desired = TimeSpan.FromSeconds(1);
                    MarkDirty(Dirty.Submodules, desired);
                    MarkDirty(Dirty.WorkingCopy, desired);
                }
            }
            else if (name.Equals("MERGE_HEAD", StringComparison.Ordinal) ||
                name.Equals("AUTO_MERGE", StringComparison.Ordinal))
            {
                if (_repo.MayHaveSubmodules())
                    MarkDirty(Dirty.Submodules, TimeSpan.FromSeconds(1));
            }
            else if (name.StartsWith("refs/tags", StringComparison.Ordinal))
            {
                MarkDirty(Dirty.Tags, TimeSpan.FromSeconds(.5));
            }
            else if (name.StartsWith("refs/stash", StringComparison.Ordinal) || name.Equals("logs/refs/stash", StringComparison.Ordinal))
            {
                MarkDirty(Dirty.Stashes, TimeSpan.FromSeconds(.5));
            }
            else if (name.Equals("HEAD", StringComparison.Ordinal) ||
                name.Equals("BISECT_START", StringComparison.Ordinal) ||
                name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                name.StartsWith("refs/remotes/", StringComparison.Ordinal) ||
                (name.StartsWith("worktrees/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                MarkDirty(Dirty.Branch, TimeSpan.FromSeconds(.5));
            }
            else if (name.StartsWith("reftable/", StringComparison.Ordinal))
            {
                var desired = TimeSpan.FromSeconds(.5);
                MarkDirty(Dirty.Branch, desired);
                MarkDirty(Dirty.Tags, desired);
                MarkDirty(Dirty.Stashes, desired);
            }
            else if (name.StartsWith("objects/", StringComparison.Ordinal) || name.Equals("index", StringComparison.Ordinal))
            {
                MarkDirty(Dirty.WorkingCopy, TimeSpan.FromSeconds(1));
            }
        }

        private void HandleWorkingCopyFileChanged(string name, string fullpath)
        {
            if (name.StartsWith(".vs/", StringComparison.Ordinal))
                return;

            if (name.Equals(".gitmodules", StringComparison.Ordinal))
            {
                var desired = TimeSpan.FromSeconds(1);
                MarkDirty(Dirty.Submodules, desired);
                MarkDirty(Dirty.WorkingCopy, desired);
                return;
            }

            var dir = SourceGit.Remote.RepositoryDirectory.Exists(fullpath) ? fullpath : SourceGit.Remote.RepositoryPath.GetDirectoryName(fullpath);
            if (IsInSubmodule(dir))
            {
                MarkDirty(Dirty.Submodules, TimeSpan.FromSeconds(1));
                return;
            }

            MarkDirty(Dirty.WorkingCopy, TimeSpan.FromSeconds(1));
        }

        private bool IsInSubmodule(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Equals(_root, StringComparison.Ordinal))
                return false;

            if (SourceGit.Remote.RepositoryFile.Exists($"{folder}/.git"))
                return true;

            return IsInSubmodule(SourceGit.Remote.RepositoryPath.GetDirectoryName(folder));
        }

    }
}
