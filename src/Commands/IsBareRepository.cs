using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsBareRepository : Command
    {
        public IsBareRepository(string path)
        {
            WorkingDirectory = path;
            Args = ["rev-parse", "--is-bare-repository"];
        }

        public bool GetResult()
        {
            if (!HasBareRepositoryLayout())
                return false;

            var rs = ReadToEnd();
            return rs.IsSuccess && rs.StdOut.Trim() == "true";
        }

        public async Task<bool> GetResultAsync()
        {
            if (!HasBareRepositoryLayout())
                return false;

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return rs.IsSuccess && rs.StdOut.Trim() == "true";
        }

        private bool HasBareRepositoryLayout()
        {
            if (GitService.RequiresGitBareRepositoryProbe(WorkingDirectory))
            {
                // For WSL repos, check via UNC path (fast, no git command)
                if (Models.WslRepositoryPath.TryParse(WorkingDirectory, out var wslPath))
                {
                    var wslGitDir = $"{wslPath.LinuxPath.TrimEnd('/')}/.git";
                    var uncPath = wslPath.ToWindowsPath(wslGitDir);
                    return !System.IO.Directory.Exists(uncPath);
                }
                return true;
            }

            return GitService.DirectoryExists(Path.Combine(WorkingDirectory, "refs")) &&
                GitService.DirectoryExists(Path.Combine(WorkingDirectory, "objects")) &&
                GitService.FileExists(Path.Combine(WorkingDirectory, "HEAD"));
        }
    }
}
