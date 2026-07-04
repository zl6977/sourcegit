using System.Collections.Generic;

namespace SourceGit.Commands
{
    public class Merge : Command
    {
        public Merge(string repo, string source, string mode, bool edit)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["merge", "--progress", edit ? "--edit" : "--no-edit", source, mode];
        }

        public Merge(string repo, List<string> targets, bool autoCommit, string strategy)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["merge", "--progress"];
            if (!string.IsNullOrEmpty(strategy))
                Args.Add($"--strategy={strategy}");
            if (!autoCommit)
                Args.Add("--no-commit");
            Args.AddRange(targets);
        }
    }
}
