using System.Collections.Generic;
namespace SourceGit.Commands
{
    public class Reset : Command
    {
        public Reset(string repo, string revision, string mode)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "reset";
            Args = ["reset"];
            if (!string.IsNullOrWhiteSpace(mode))
                Args.Add(mode);
            if (!string.IsNullOrWhiteSpace(revision))
                Args.Add(revision);
        }

        public Reset(string repo, List<string> paths)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "reset";
            Args = new List<string> { "reset", "--" };
            Args.AddRange(paths);
        }
    }
}
