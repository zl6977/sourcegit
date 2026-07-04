using System.Collections.Generic;

namespace SourceGit.Commands
{
    public class Remove : Command
    {
        public Remove(string repo, List<string> files)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["rm", "-f", "--"];
            Args.AddRange(files);
        }
    }
}
