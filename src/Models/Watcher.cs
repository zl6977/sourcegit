using System;
using System.Collections.Generic;
using System.IO;
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
                Interlocked.Increment(ref _target._lockCount);
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref _target._lockCount);
            }

            private Watcher _target;
        }

        public Watcher(IRepository repo, string fullpath, string gitDir)
        {
            _repo = repo;
            _root = new SourceGit.Remote.RepositoryDirectoryInfo(fullpath).FullName;
            _watchers = new List<FileSystemWatcher>();

            if (SourceGit.Remote.RepositoryPath.IsRemote(fullpath))
            {
                _remoteSubscription = SourceGit.Remote.RemoteWorkspace.ForPath(fullpath).Subscribe(fullpath, () =>
                {
                    Interlocked.Exchange(ref _updateBranch, 1);
                    Interlocked.Exchange(ref _updateWC, 1);
                    Interlocked.Exchange(ref _updateTags, 1);
                    Interlocked.Exchange(ref _updateStashes, 1);
                    Interlocked.Exchange(ref _updateSubmodules, 1);
                });
                _timer = new Timer(Tick, null, 100, 100);
                return;
            }

            try
            {
                var testGitDir = new SourceGit.Remote.RepositoryDirectoryInfo(SourceGit.Remote.RepositoryPath.Combine(fullpath, ".git")).FullName;
                var desiredDir = new SourceGit.Remote.RepositoryDirectoryInfo(gitDir).FullName;
                if (testGitDir.Equals(desiredDir, StringComparison.Ordinal))
                {
                    var combined = new FileSystemWatcher();
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

                    _watchers.Add(combined);
                }
                else
                {
                    var wc = new FileSystemWatcher();
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

                    _watchers.Add(wc);
                    _watchers.Add(git);
                }
            }
            catch (System.Exception)
            {
                // The path cannot be watched at all (e.g. //wsl.localhost UNC shares reject
                // ReadDirectoryChangesW): Tick falls back to polling refresh.
                Interlocked.Exchange(ref _degraded, 1);
            }

            _timer = new Timer(Tick, null, 100, 100);

            // Starts filesystem watchers in another thread to avoid UI blocking
            Task.Run(() =>
            {
                try
                {
                    foreach (var watcher in _watchers)
                        watcher.EnableRaisingEvents = true;
                }
                catch (System.Exception)
                {
                    // EnableRaisingEvents can fail outright on paths that reject ReadDirectoryChangesW
                    // (e.g. //wsl.localhost UNC shares); fall back to polling. Also occurs while `Dispose` runs.
                    Interlocked.Exchange(ref _degraded, 1);
                }
            });
        }

        public IDisposable Lock()
        {
            return new LockContext(this);
        }

        public void MarkBranchUpdated()
        {
            Interlocked.Exchange(ref _updateBranch, 0);
            Interlocked.Exchange(ref _updateWC, 0);
        }

        public void MarkTagUpdated()
        {
            Interlocked.Exchange(ref _updateTags, 0);
        }

        public void MarkWorkingCopyUpdated()
        {
            Interlocked.Exchange(ref _updateWC, 0);
        }

        public void MarkStashUpdated()
        {
            Interlocked.Exchange(ref _updateStashes, 0);
        }

        public void MarkSubmodulesUpdated()
        {
            Interlocked.Exchange(ref _updateSubmodules, 0);
        }

        public void Dispose()
        {
            _remoteSubscription?.Dispose();
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            _timer.Dispose();
            _timer = null;
        }

        private IDisposable _remoteSubscription;
        // How often Tick re-checks a repository whose filesystem watchers failed to watch it
        // (e.g. //wsl.localhost UNC shares, which reject ReadDirectoryChangesW). Local disks keep
        // event-driven refresh; polling is only the fallback once a watcher reports it is dead.
        private int _pollIntervalSeconds = 30;
        public int PollIntervalSeconds
        {
            get => _pollIntervalSeconds;
            set
            {
                _pollIntervalSeconds = value;
                _nextPollFileTime = 0; // recompute the next deadline with the new interval
            }
        }

        private void OnWatcherError(object o, ErrorEventArgs e)
        {
            Interlocked.Exchange(ref _degraded, 1);
        }
        private static void MaxFlag(ref long location, long value)
        {
            var old = Interlocked.Read(ref location);
            while (value > old && Interlocked.CompareExchange(ref location, value, old) != old)
                old = Interlocked.Read(ref location);
        }


        private void Tick(object sender)
        {
            if (Interlocked.Read(ref _lockCount) > 0)
                return;

            var now = DateTime.Now.ToFileTime();
            if (Interlocked.Read(ref _degraded) > 0)
            {
                var next = _nextPollFileTime;
                if (next == 0)
                    next = _nextPollFileTime = now + PollIntervalSeconds * 10_000_000L;
                if (now >= next)
                {
                    _nextPollFileTime = now + PollIntervalSeconds * 10_000_000L;
                    var at = now - 1; // strictly older than this tick so the flags process below
                    MaxFlag(ref _updateBranch, at);
                    MaxFlag(ref _updateWC, at);
                    MaxFlag(ref _updateTags, at);
                    MaxFlag(ref _updateStashes, at);
                    MaxFlag(ref _updateSubmodules, at);
                }
            }
            var refreshCommits = false;
            var refreshSubmodules = false;
            var refreshWC = false;

            var oldUpdateBranch = Interlocked.Exchange(ref _updateBranch, -1);
            if (oldUpdateBranch > 0)
            {
                if (now > oldUpdateBranch)
                {
                    refreshCommits = true;
                    refreshSubmodules = _repo.MayHaveSubmodules();
                    refreshWC = true;

                    _repo.RefreshBranches();
                    _repo.RefreshWorktrees();
                }
                else
                {
                    Interlocked.CompareExchange(ref _updateBranch, oldUpdateBranch, -1);
                }
            }

            if (refreshWC)
            {
                Interlocked.Exchange(ref _updateWC, -1);
                _repo.RefreshWorkingCopyChanges();
            }
            else
            {
                var oldUpdateWC = Interlocked.Exchange(ref _updateWC, -1);
                if (oldUpdateWC > 0)
                {
                    if (now > oldUpdateWC)
                        _repo.RefreshWorkingCopyChanges();
                    else
                        Interlocked.CompareExchange(ref _updateWC, oldUpdateWC, -1);
                }
            }

            if (refreshSubmodules)
            {
                Interlocked.Exchange(ref _updateSubmodules, -1);
                _repo.RefreshSubmodules();
            }
            else
            {
                var oldUpdateSubmodule = Interlocked.Exchange(ref _updateSubmodules, -1);
                if (oldUpdateSubmodule > 0)
                {
                    if (now > oldUpdateSubmodule)
                        _repo.RefreshSubmodules();
                    else
                        Interlocked.CompareExchange(ref _updateSubmodules, oldUpdateSubmodule, -1);
                }
            }

            var oldUpdateStashes = Interlocked.Exchange(ref _updateStashes, -1);
            if (oldUpdateStashes > 0)
            {
                if (now > oldUpdateStashes)
                    _repo.RefreshStashes();
                else
                    Interlocked.CompareExchange(ref _updateStashes, oldUpdateStashes, -1);
            }

            var oldUpdateTags = Interlocked.Exchange(ref _updateTags, -1);
            if (oldUpdateTags > 0)
            {
                if (now > oldUpdateTags)
                {
                    refreshCommits = true;
                    _repo.RefreshTags();
                }
                else
                {
                    Interlocked.CompareExchange(ref _updateTags, oldUpdateTags, -1);
                }
            }

            if (refreshCommits)
                _repo.RefreshCommits();
        }

        private void OnRepositoryChanged(object o, FileSystemEventArgs e)
        {
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
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            HandleGitDirFileChanged(name);
        }

        private void OnWorkingCopyChanged(object o, FileSystemEventArgs e)
        {
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
                    var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                    Interlocked.Exchange(ref _updateSubmodules, desired);
                    Interlocked.Exchange(ref _updateWC, desired);
                }
            }
            else if (name.Equals("MERGE_HEAD", StringComparison.Ordinal) ||
                name.Equals("AUTO_MERGE", StringComparison.Ordinal))
            {
                if (_repo.MayHaveSubmodules())
                    Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
            }
            else if (name.StartsWith("refs/tags", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateTags, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.StartsWith("refs/stash", StringComparison.Ordinal) || name.Equals("logs/refs/stash", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateStashes, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.Equals("HEAD", StringComparison.Ordinal) ||
                name.Equals("BISECT_START", StringComparison.Ordinal) ||
                name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                name.StartsWith("refs/remotes/", StringComparison.Ordinal) ||
                (name.StartsWith("worktrees/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                Interlocked.Exchange(ref _updateBranch, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.StartsWith("reftable/", StringComparison.Ordinal))
            {
                var desired = DateTime.Now.AddSeconds(.5).ToFileTime();
                Interlocked.Exchange(ref _updateBranch, desired);
                Interlocked.Exchange(ref _updateTags, desired);
                Interlocked.Exchange(ref _updateStashes, desired);
            }
            else if (name.StartsWith("objects/", StringComparison.Ordinal) || name.Equals("index", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
            }
        }

        private void HandleWorkingCopyFileChanged(string name, string fullpath)
        {
            if (name.StartsWith(".vs/", StringComparison.Ordinal))
                return;

            if (name.Equals(".gitmodules", StringComparison.Ordinal))
            {
                var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                Interlocked.Exchange(ref _updateSubmodules, desired);
                Interlocked.Exchange(ref _updateWC, desired);
                return;
            }

            var dir = SourceGit.Remote.RepositoryDirectory.Exists(fullpath) ? fullpath : SourceGit.Remote.RepositoryPath.GetDirectoryName(fullpath);
            if (IsInSubmodule(dir))
            {
                Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
                return;
            }

            Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
        }

        private bool IsInSubmodule(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Equals(_root, StringComparison.Ordinal))
                return false;

            if (SourceGit.Remote.RepositoryFile.Exists($"{folder}/.git"))
                return true;

            return IsInSubmodule(SourceGit.Remote.RepositoryPath.GetDirectoryName(folder));
        }

        private readonly IRepository _repo;
        private readonly string _root;
        private List<FileSystemWatcher> _watchers;
        private Timer _timer;

        private long _lockCount;
        private long _degraded;
        private long _nextPollFileTime;
        private long _updateWC;
        private long _updateBranch;
        private long _updateSubmodules;
        private long _updateStashes;
        private long _updateTags;
    }
}
