using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace SourceGit.Models
{
    /// <summary>
    /// Monitors repository changes via polling instead of filesystem events.
    /// This works uniformly for both Windows and WSL repositories.
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

            private Watcher _target;
        }

        /// <summary>
        /// Polling interval in milliseconds.
        /// </summary>
        private const int POLL_INTERVAL_MS = 1000;

        public Watcher(IRepository repo, string fullpath, string gitDir)
        {
            _repo = repo;
            _repoPath = fullpath;

            // Initialize baseline on first tick to avoid blocking startup
            _needInit = true;

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
            // First tick: initialize baseline values
            if (_needInit)
            {
                _previousHeadHash = QueryHeadHash();
                _previousIndexHash = QueryIndexHash();
                _previousStashHash = QueryStashHash();
                _previousTagCount = QueryTagCount();
                _needInit = false;
                return;
            }

            var refreshCommits = false;
            var refreshSubmodules = false;
            // Check if HEAD changed (branch/commit)
            var currentHeadHash = QueryHeadHash();
            if (!string.Equals(currentHeadHash, _previousHeadHash, StringComparison.Ordinal))
            {
                refreshCommits = true;
                _previousHeadHash = currentHeadHash;
                _repo.RefreshBranches();
                _repo.RefreshWorktrees();

                if (_repo.MayHaveSubmodules())
                    refreshSubmodules = true;
            }

            // Check if index changed
            var currentIndexHash = QueryIndexHash();
            if (!string.Equals(currentIndexHash, _previousIndexHash, StringComparison.Ordinal))
            {
                _previousIndexHash = currentIndexHash;
                _repo.RefreshWorkingCopyChanges();
            }

            // Check if working copy changed
            if (!refreshCommits)
            {
                if (QueryHasWorkingCopyChanges())
                    _repo.RefreshWorkingCopyChanges();
            }

            // Check if submodules changed
            if (refreshSubmodules || _submoduleUpdated)
            {
                _submoduleUpdated = false;
                _repo.RefreshSubmodules();
            }

            // Check if stash changed
            var currentStashHash = QueryStashHash();
            if (!string.Equals(currentStashHash, _previousStashHash, StringComparison.Ordinal))
            {
                _previousStashHash = currentStashHash;
                _repo.RefreshStashes();
            }

            // Check if tags changed
            var currentTagCount = QueryTagCount();
            if (currentTagCount != _previousTagCount)
            {
                _previousTagCount = currentTagCount;
                _repo.RefreshTags();
                refreshCommits = true;
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

        private int QueryTagCount()
        {
            try
            {
                var result = new Commands.GitQuery(_repoPath, "tag", "-l").GetResult();
                return result.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch
            {
                return 0;
            }
        }

        private bool QueryHasWorkingCopyChanges()
        {
            try
            {
                var exitCode = new Commands.GitQuery(_repoPath, "diff-index", "--quiet", "HEAD", "--").GetExitCode();
                return exitCode != 0;
            }
            catch
            {
                return false;
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
        private bool _needInit = true;
        private Timer _timer;

        private string _previousHeadHash = string.Empty;
        private string _previousIndexHash = string.Empty;
        private string _previousStashHash = string.Empty;
        private int _previousTagCount;

        private bool _branchUpdated;
        private bool _submoduleUpdated;

        private long _lockCount;
        private int _isRunning; // 0 = not running, 1 = running (timer tick)
    }
}
