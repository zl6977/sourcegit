using System.Collections.Generic;
namespace SourceGit.Commands
{
    public class Restore : Command
    {

        public Restore(string repo, string revision, string mode)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "reset";
            Args = ["reset", revision, mode];
        }

        public Restore(string repo, List<string> paths)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "restore";
            Args = new List<string> { "restore", "--progress", "--worktree", "--recurse-submodules", "--" };
            Args.AddRange(paths);
        }
    }
}
