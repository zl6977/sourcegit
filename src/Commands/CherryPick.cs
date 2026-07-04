using System.Collections.Generic;

namespace SourceGit.Commands
{
    public class CherryPick : Command
    {
        public CherryPick(string repo, List<string> commits, bool noCommit, bool appendSourceToMessage, int mainline)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["cherry-pick"];
            if (noCommit)
                Args.Add("-n");
            if (appendSourceToMessage)
                Args.Add("-x");
            if (mainline > 0)
            {
                Args.Add("-m");
                Args.Add(mainline.ToString());
            }
            Args.AddRange(commits);
        }
    }
}
