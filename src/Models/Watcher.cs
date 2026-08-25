using System;
using System.Threading;

namespace SourceGit.Models
{
    /// <summary>
    /// Monitors repository changes by polling the git state every 30 seconds.
    /// Only the currently visible repository is polled; use the manual refresh
    /// (F5) for real-time updates. All queries are translated to CLI and executed
    /// through the git backend (a single batched WSL invocation for WSL repositories).
    /// </summary>
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

            private Watcher _target = null;
        }

        /// <summary>
        /// Polling interval in milliseconds.
        /// </summary>
        private const int POLL_INTERVAL_MS = 30000;

        public Watcher(IRepository repo, string fullpath)
        {
            _repo = repo;
            _repoPath = fullpath;

            if (WslRepositoryPath.TryParse(fullpath, out var wslPath))
            {
                _isWsl = true;
                _wslPath = wslPath;
            }

            _timer = new Timer(Tick, null, POLL_INTERVAL_MS, POLL_INTERVAL_MS);
        }

        public IDisposable Lock()
        {
            return new LockContext(this);
        }

        public void MarkBranchUpdated()
        {
            _branchUpdated = true;
        }

        public void MarkSubmodulesUpdated()
        {
            _submoduleUpdated = true;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void Tick(object sender)
        {
            if (Interlocked.Read(ref _lockCount) > 0)
                return;

            // Only the currently visible repository is polled.
            bool isActive;
            try
            {
                isActive = _repo.IsActive();
            }
            catch
            {
                isActive = false;
            }
            if (!isActive)
                return;

            // Prevent concurrent timer ticks - if a previous tick is still running, skip this one
            if (Interlocked.Exchange(ref _isRunning, 1) == 1)
                return;

            try
            {
                CheckForChanges();
            }
            catch
            {
                // Ignore all exceptions in the polling loop
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        }

        private void CheckForChanges()
        {
            string headHash, indexHash, stashHash, refHash;
            bool hasWCChanges;

            if (_isWsl)
                (headHash, indexHash, stashHash, refHash, hasWCChanges) = QueryWslState();
            else
            {
                headHash = QueryHeadHash();
                indexHash = QueryIndexHash();
                stashHash = QueryStashHash();
                refHash = QueryRefHash();
                hasWCChanges = QueryHasWorkingCopyChanges();
            }

            // First tick: initialize baseline values
            if (_needInit)
            {
                _previousHeadHash = headHash;
                _previousIndexHash = indexHash;
                _previousStashHash = stashHash;
                _previousRefHash = refHash;
                _needInit = false;
                return;
            }

            var refreshCommits = false;
            var refreshSubmodules = false;

            // Check if HEAD changed (branch/commit)
            if (!string.Equals(headHash, _previousHeadHash, StringComparison.Ordinal))
            {
                refreshCommits = true;
                _previousHeadHash = headHash;
                _repo.RefreshBranches();
                _repo.RefreshWorktrees();

                if (_repo.MayHaveSubmodules())
                    refreshSubmodules = true;
            }

            // Check if branches or tags changed
            if (!string.Equals(refHash, _previousRefHash, StringComparison.Ordinal))
            {
                _previousRefHash = refHash;
                _repo.RefreshBranches();
                _repo.RefreshWorktrees();
                _repo.RefreshTags();

                if (_repo.MayHaveSubmodules())
                    refreshSubmodules = true;
                refreshCommits = true;
            }

            // Check if index changed
            if (!string.Equals(indexHash, _previousIndexHash, StringComparison.Ordinal))
            {
                _previousIndexHash = indexHash;
                _repo.RefreshWorkingCopyChanges();
            }

            // Check if working copy changed
            else if (!refreshCommits && hasWCChanges)
                _repo.RefreshWorkingCopyChanges();

            // Check if submodules changed
            if (refreshSubmodules || _submoduleUpdated)
            {
                _submoduleUpdated = false;
                _repo.RefreshSubmodules();
            }

            // Check if stash changed
            if (!string.Equals(stashHash, _previousStashHash, StringComparison.Ordinal))
            {
                _previousStashHash = stashHash;
                _repo.RefreshStashes();
            }

            // Check for externally modified branches
            if (_branchUpdated)
            {
                _branchUpdated = false;
                _repo.RefreshBranches();
                _repo.RefreshWorktrees();
                refreshCommits = true;
            }

            if (refreshCommits)
                _repo.RefreshCommits();
        }

        private string QueryHeadHash()
        {
            try
            {
                return new Commands.GitQuery(_repoPath, "rev-parse", "--verify", "HEAD").GetResult().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private string QueryIndexHash()
        {
            try
            {
                var result = new Commands.GitQuery(_repoPath, "ls-files", "--stage").GetResult();
                return !string.IsNullOrEmpty(result) ? GetQuickHash(result) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string QueryStashHash()
        {
            try
            {
                return new Commands.GitQuery(_repoPath, "rev-parse", "--verify", "refs/stash").GetResult().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private string QueryRefHash()
        {
            try
            {
                var result = new Commands.GitQuery(_repoPath, "for-each-ref", "refs/heads/", "refs/remotes/", "refs/tags", "--format=%(refname)").GetResult();
                return GetQuickHash(result);
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool QueryHasWorkingCopyChanges()
        {
            try
            {
                return new Commands.GitQuery(_repoPath, "diff-index", "--quiet", "HEAD", "--").GetExitCode() != 0;
            }
            catch
            {
                return false;
            }
        }

        private (string, string, string, string, bool) QueryWslState()
        {
            try
            {
                using var batch = new Commands.WslBatchExecutor(_wslPath);
                var results = batch.Execute(
                [
                    ["rev-parse", "--verify", "HEAD"],
                    ["ls-files", "--stage"],
                    ["rev-parse", "--verify", "refs/stash"],
                    ["for-each-ref", "refs/heads/", "refs/remotes/", "refs/tags", "--format=%(refname)"],
                    ["status", "--porcelain"],
                ]);

                return (
                    results[0]?.Trim() ?? string.Empty,
                    string.IsNullOrEmpty(results[1]) ? string.Empty : GetQuickHash(results[1]),
                    results[2]?.Trim() ?? string.Empty,
                    GetQuickHash(results[3] ?? string.Empty),
                    !string.IsNullOrEmpty(results[4]));
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty, string.Empty, false);
            }
        }

        private static string GetQuickHash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in text)
                {
                    hash ^= (uint)c;
                    hash *= 16777619;
                }
                return hash.ToString();
            }
        }

        private readonly IRepository _repo;
        private readonly string _repoPath;
        private readonly bool _isWsl;
        private readonly WslRepositoryPath _wslPath = null;
        private Timer _timer;

        private bool _needInit = true;
        private string _previousHeadHash = string.Empty;
        private string _previousIndexHash = string.Empty;
        private string _previousStashHash = string.Empty;
        private string _previousRefHash = string.Empty;
        private bool _branchUpdated;
        private bool _submoduleUpdated;

        private long _lockCount;
        private int _isRunning; // 0 = not running, 1 = running (timer tick)
    }
}
