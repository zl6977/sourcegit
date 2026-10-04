using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsBareRepository : Command
    {
        public IsBareRepository(string path)
        {
            WorkingDirectory = path;
            Args = "rev-parse --is-bare-repository";
        }

        public bool GetResult()
        {
            if (!SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "refs")) ||
                !SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "objects")) ||
                !SourceGit.Remote.RepositoryFile.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "HEAD")))
                return false;

            var rs = ReadToEnd();
            return rs.IsSuccess && rs.StdOut.Trim() == "true";
        }

        public async Task<bool> GetResultAsync()
        {
            if (!SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "refs")) ||
                !SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "objects")) ||
                !SourceGit.Remote.RepositoryFile.Exists(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, "HEAD")))
                return false;

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return rs.IsSuccess && rs.StdOut.Trim() == "true";
        }
    }
}
