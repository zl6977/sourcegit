using System;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class BackendTempFile : IDisposable
    {
        private readonly string _repo;

        private BackendTempFile(string repo, string file)
        {
            _repo = repo;
            File = file;
        }

        public string File { get; }

        public static Task<BackendTempFile> CreateAsync(string repo, string content, string prefix = "sourcegit_tmp")
        {
            var gitDir = new QueryGitDir(repo).GetResult();
            if (string.IsNullOrEmpty(gitDir))
                gitDir = repo;

            var file = Path.Combine(gitDir, $"{prefix}_{Guid.NewGuid():N}");
            GitService.WriteFile(file, content, repo);
            return Task.FromResult(new BackendTempFile(repo, file));
        }

        public void Dispose()
        {
            try
            {
                GitService.DeleteFile(File, _repo);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
