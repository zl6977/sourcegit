using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class WorkingCopyOperations
    {
        public static Task<Models.Commit> QueryHeadAsync(Repository repo)
        {
            return new Commands.QuerySingleCommit(repo.FullPath, "HEAD").GetResultAsync();
        }

        public static async Task AssumeUnchangedAsync(Repository repo, Models.Change change)
        {
            var log = repo.CreateLog("Assume File Unchanged");
            await new Commands.AssumeUnchanged(repo.FullPath, change.Path, true).Use(log).ExecAsync();
            log.Complete();
        }

        public static bool IsLFSFiltered(Repository repo, Models.Change change)
        {
            return new Commands.IsLFSFiltered(repo.FullPath, change.Path).GetResult();
        }

        public static bool CanUseBuiltinMergeTool(string fullPath, Models.Change change)
        {
            return change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified &&
                !RepositoryFileService.DirectoryExists(fullPath);
        }
    }
}
