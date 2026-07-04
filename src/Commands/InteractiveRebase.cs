namespace SourceGit.Commands
{
    public class InteractiveRebase : Command
    {
        public InteractiveRebase(string repo, string basedOn, bool autoStash, bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;
            Editor = EditorType.RebaseEditor;

            Args = ["-c", "core.commentChar=±", "rebase", "-i", "--autosquash"];
            if (autoStash)
                Args.Add("--autostash");
            if (noVerify)
                Args.Add("--no-verify");
            Args.Add(basedOn);
        }
    }
}
