using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class RevisionFileOperations
    {
        public enum Side
        {
            Left,
            Right,
        }

        public static async Task ResetPathToRevisionAsync(Repository repo, string path, string revision, string description)
        {
            var log = repo.CreateLog($"Reset File to '{description}'");
            await new Commands.Checkout(repo.FullPath).Use(log).FileWithRevisionAsync(path, revision);
            log.Complete();
        }

        public static async Task ResetChangeToRevisionAsync(Repository repo, Models.Change change, string revision, string description, Side side)
        {
            var paths = BuildResetPaths(repo, [change], side);
            var log = repo.CreateLog($"Reset File to '{description}'");
            await ExecuteResetAsync(repo, paths, revision, log);
            log.Complete();
        }

        public static async Task ResetChangesToRevisionAsync(Repository repo, List<Models.Change> changes, string revision, string description, Side side)
        {
            var paths = BuildResetPaths(repo, changes, side);
            var log = repo.CreateLog($"Reset Files to '{description}'");
            await ExecuteResetAsync(repo, paths, revision, log);
            log.Complete();
        }

        private static ResetPaths BuildResetPaths(Repository repo, List<Models.Change> changes, Side side)
        {
            var paths = new ResetPaths();

            foreach (var change in changes)
            {
                if (side == Side.Left)
                    AddLeftResetPaths(repo, change, paths);
                else
                    AddRightResetPaths(repo, change, paths);
            }

            return paths;
        }

        private static void AddLeftResetPaths(Repository repo, Models.Change change, ResetPaths paths)
        {
            if (change.Index == Models.ChangeState.Added)
            {
                if (repo.Controller.FileExists(change.Path))
                    paths.Removes.Add(change.Path);
            }
            else if (change.Index == Models.ChangeState.Renamed)
            {
                if (repo.Controller.FileExists(change.Path))
                    paths.Removes.Add(change.Path);

                paths.Checkouts.Add(change.OriginalPath);
            }
            else
            {
                paths.Checkouts.Add(change.Path);
            }
        }

        private static void AddRightResetPaths(Repository repo, Models.Change change, ResetPaths paths)
        {
            if (change.Index == Models.ChangeState.Deleted)
            {
                if (repo.Controller.FileExists(change.Path))
                    paths.Removes.Add(change.Path);
            }
            else if (change.Index == Models.ChangeState.Renamed)
            {
                if (repo.Controller.FileExists(change.OriginalPath))
                    paths.Removes.Add(change.OriginalPath);

                paths.Checkouts.Add(change.Path);
            }
            else
            {
                paths.Checkouts.Add(change.Path);
            }
        }

        private static async Task ExecuteResetAsync(Repository repo, ResetPaths paths, string revision, Models.ICommandLog log)
        {
            if (paths.Removes.Count > 0)
                await new Commands.Remove(repo.FullPath, paths.Removes)
                    .Use(log)
                    .ExecAsync();

            if (paths.Checkouts.Count == 1)
                await new Commands.Checkout(repo.FullPath)
                    .Use(log)
                    .FileWithRevisionAsync(paths.Checkouts[0], revision);
            else if (paths.Checkouts.Count > 1)
                await new Commands.Checkout(repo.FullPath)
                    .Use(log)
                    .MultipleFilesWithRevisionAsync(paths.Checkouts, revision);
        }

        private class ResetPaths
        {
            public List<string> Removes { get; } = [];
            public List<string> Checkouts { get; } = [];
        }
    }
}
