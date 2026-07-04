using System;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Provides all git repository I/O operations through a single backend-aware interface.
    /// This is the sole interface between the frontend (ViewModels) and git storage.
    /// The frontend should never call System.IO directly on repo paths.
    /// </summary>
    public class RepositoryController
    {
        private readonly string _gitCommonDir;

        public RepositoryController(string fullPath, string gitDir)
        {
            FullPath = fullPath;
            GitDir = gitDir;

            // For worktrees, resolve the common directory via git command
            var isWorktree = gitDir.IndexOf("/worktrees/", StringComparison.Ordinal) > 0;
            if (isWorktree)
            {
                var commonDirFile = Path.Combine(gitDir, "commondir");
                if (GitService.FileExists(commonDirFile))
                {
                    var commonDir = GitService.ReadFile(commonDirFile, fullPath).Trim();
                    if (Path.IsPathRooted(commonDir))
                        commonDir = GitService.NormalizeRepositoryPath(commonDir, fullPath);
                    else
                        commonDir = GitService.NormalizeRepositoryPath(Path.Combine(gitDir, commonDir), fullPath);

                    _gitCommonDir = commonDir.Replace('\\', '/').TrimEnd('/');
                    return;
                }
            }

            _gitCommonDir = gitDir;
        }

        /// <summary>
        /// The repository root path (normalized to forward slashes).
        /// </summary>
        public string FullPath { get; }

        /// <summary>
        /// The .git directory path (normalized to forward slashes).
        /// </summary>
        public string GitDir { get; }

        /// <summary>
        /// The git common directory (same as GitDir for non-worktrees).
        /// </summary>
        public string GitCommonDir => _gitCommonDir;

        #region File Operations

        public bool FileExists(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return GitService.FileExists(path);
        }

        public bool GitDirFileExists(string fileName)
        {
            var path = Path.Combine(GitDir, fileName);
            return GitService.FileExists(path);
        }

        public bool GitCommonDirFileExists(string fileName)
        {
            var path = Path.Combine(_gitCommonDir, fileName);
            return GitService.FileExists(path);
        }

        public bool DirectoryExists(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return GitService.DirectoryExists(path);
        }

        public string ReadFile(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return GitService.ReadFile(path, FullPath);
        }

        public void WriteFile(string repoRelativePath, string content)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            GitService.WriteFile(path, content, FullPath);
        }

        public void DeleteFile(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            GitService.DeleteFile(path, FullPath);
        }

        public string ReadGitDirFile(string fileName)
        {
            var path = Path.Combine(GitDir, fileName);
            return GitService.ReadFile(path, FullPath);
        }

        public string ReadGitCommonDirFile(string fileName)
        {
            var path = Path.Combine(_gitCommonDir, fileName);
            return GitService.ReadFile(path, FullPath);
        }

        public void WriteGitDirFile(string fileName, string content)
        {
            var path = Path.Combine(GitDir, fileName);
            GitService.WriteFile(path, content, FullPath);
        }

        public void DeleteGitDirFile(string fileName)
        {
            var path = Path.Combine(GitDir, fileName);
            GitService.DeleteFile(path, FullPath);
        }

        public void DeleteGitDirDirectory(string subPath)
        {
            var path = Path.Combine(GitDir, subPath);
            GitService.DeleteDirectory(path, FullPath);
        }

        public void WriteGitDirFileAndReplace(string fileName, string content)
        {
            var path = Path.Combine(GitDir, fileName);
            GitService.WriteFileAndReplace(path, content, FullPath);
        }

        public void WriteGitCommonDirFileAndReplace(string fileName, string content)
        {
            var path = Path.Combine(_gitCommonDir, fileName);
            GitService.WriteFileAndReplace(path, content, FullPath);
        }

        public Stream OpenReadGitDirFile(string fileName)
        {
            var path = Path.Combine(GitDir, fileName);
            return GitService.OpenRead(path, FullPath);
        }

        public async Task<string> ReadFileAsync(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return await System.IO.File.ReadAllTextAsync(path);
        }

        public async Task WriteFileAsync(string repoRelativePath, string content)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            await System.IO.File.WriteAllTextAsync(path, content);
        }

        public async Task AppendAllTextAsync(string repoRelativePath, string content)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            await System.IO.File.AppendAllTextAsync(path, content);
        }

        public async Task WriteAllLinesAsync(string repoRelativePath, System.Collections.Generic.IEnumerable<string> lines)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            await System.IO.File.WriteAllLinesAsync(path, lines);
        }

        public long GetFileSize(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return new System.IO.FileInfo(path).Length;
        }

        public Stream OpenRead(string repoRelativePath)
        {
            var path = Path.Combine(FullPath, repoRelativePath);
            return GitService.OpenRead(path, FullPath);
        }
        #endregion

        #region Repository State Checks

        public bool HasMergeHead()
        {
            return GitDirFileExists("MERGE_HEAD");
        }

        public bool HasCherryPickHead()
        {
            return GitDirFileExists("CHERRY_PICK_HEAD");
        }

        public bool HasRevertHead()
        {
            return GitDirFileExists("REVERT_HEAD");
        }

        public bool HasRebaseMerge()
        {
            return GitService.DirectoryExists(Path.Combine(GitDir, "rebase-merge"));
        }

        public bool HasRebaseApply()
        {
            return GitService.DirectoryExists(Path.Combine(GitDir, "rebase-apply"));
        }

        public bool HasBisectStart()
        {
            return GitDirFileExists("BISECT_START");
        }
        public bool GitDirDirectoryExists(string subPath)
        {
            var path = Path.Combine(GitDir, subPath);
            return GitService.DirectoryExists(path);
        }

        public System.IO.FileInfo[] GetGitDirFiles(string subDir)
        {
            var path = Path.Combine(GitDir, subDir);
            return GitService.GetFiles(path);
        }

        public bool HasIndexLock()
        {
            return GitDirFileExists("index.lock");
        }

        /// <summary>
        /// Checks if LFS is enabled by looking for the pre-push hook.
        /// </summary>
        public bool IsLFSEnabled()
        {
            var path = Path.Combine(GitDir, "hooks", "pre-push");
            if (!GitService.FileExists(path, FullPath))
                return false;

            try
            {
                var content = GitService.ReadFile(path, FullPath);
                return content.Contains("git lfs pre-push");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if the repo may have submodules by checking .gitmodules file size.
        /// </summary>
        public bool MayHaveSubmodules()
        {
            var modulesFile = Path.Combine(FullPath, ".gitmodules");
            return GitService.FileExists(modulesFile, FullPath);
        }

        #endregion

        #region Settings

        public Models.RepositorySettings LoadSettings()
        {
            return Models.RepositorySettings.Get(_gitCommonDir, this);
        }

        public Models.RepositoryUIStates LoadUIStates()
        {
            return Models.RepositoryUIStates.Load(GitDir, this);
        }

        #endregion

        #region Directory Operations

        public string ResolvePath(string relativePath)
        {
            var fullpath = Path.Combine(FullPath, relativePath);
            if (OperatingSystem.IsWindows())
                return fullpath.Replace('/', '\\');
            return fullpath;
        }

        public string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }

        #endregion
    }
}
