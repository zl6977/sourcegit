namespace SourceGit.Commands
{
    public class Rebase : Command
    {
        public Rebase(string repo, string basedOn, bool autoStash, bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["-c", "core.commentChar=±", "rebase"];
            if (autoStash)
                Args.Add("--autostash");
            if (noVerify)
                Args.Add("--no-verify");
            Args.Add(basedOn);
        }
    }
}
