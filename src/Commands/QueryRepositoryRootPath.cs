using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRepositoryRootPath : Command
    {
        public QueryRepositoryRootPath(string path)
        {
            WorkingDirectory = path;
            Args = "rev-parse --show-toplevel";
        }

        public Result GetResult()
        {
            return Map(ReadToEnd());
        }

        public async Task<Result> GetResultAsync()
        {
            return Map(await ReadToEndAsync().ConfigureAwait(false));
        }

        private Result Map(Result result)
        {
            if (result.IsSuccess && SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory) && !string.IsNullOrWhiteSpace(result.StdOut))
                result.StdOut = SourceGit.Remote.RepositoryPath.Remote(SourceGit.Remote.RepositoryPath.ConnectionId(WorkingDirectory), result.StdOut.Trim());
            return result;
        }
    }
}
