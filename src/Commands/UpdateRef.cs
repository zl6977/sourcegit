using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class UpdateRef : Command
    {
        public UpdateRef(string repo, string refname, string revision)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            Args = ["update-ref", refname, revision];
        }

        public async Task<bool> RunAsync()
        {
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
