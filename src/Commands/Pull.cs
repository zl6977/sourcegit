using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Pull : Command
    {
        public Pull(string repo, string remote, string branch, bool useRebase)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            Args = ["pull", "--verbose", "--progress"];
            if (useRebase)
                Args.Add("--rebase=true");
            Args.Add(remote);
            // An empty branch means "pull the upstream". Do not emit an empty
            // argument: `git` tolerates it on Windows, but `wsl.exe --exec`
            // rejects it with `Wsl/Service/E_INVALIDARG`.
            if (!string.IsNullOrEmpty(branch))
                Args.Add(branch);
        }

        public async Task<bool> RunAsync()
        {
            SSHKey = await new Config(WorkingDirectory).GetAsync($"remote.{_remote}.sshkey").ConfigureAwait(false);
            return await ExecAsync().ConfigureAwait(false);
        }

        private readonly string _remote;
    }
}
