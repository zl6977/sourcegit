namespace SourceGit.Commands
{
    public class Move : Command
    {
        public Move(string repo, string oldPath, string newPath, bool force)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["mv", "-v"];
            if (force)
                Args.Add("-f");
            Args.Add(oldPath);
            Args.Add(newPath);
        }
    }
}
