using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class CommitOperations
    {
        public static Task<Models.Commit> QuerySingleAsync(string repo, string revision)
        {
            return new Commands.QuerySingleCommit(repo, revision).GetResultAsync();
        }
    }
}
