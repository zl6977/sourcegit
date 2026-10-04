using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryGitCommonDir : Command
    {
        public QueryGitCommonDir(string workDir)
        {
            WorkingDirectory = workDir;
            Args = "rev-parse --git-common-dir";
            RaiseError = false;
        }

        public async Task<string> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess || string.IsNullOrEmpty(rs.StdOut))
                return string.Empty;

            var dir = rs.StdOut.Trim();
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
                return SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, dir);
            if (SourceGit.Remote.RepositoryPath.IsPathRooted(dir))
                return dir;
            return SourceGit.Remote.RepositoryPath.GetFullPath(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, dir));
        }
    }
}
