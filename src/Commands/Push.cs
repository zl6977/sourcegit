using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Push : Command
    {
        public Push(string repo, string local, string remote, string remoteBranch, bool withTags, bool checkSubmodules, bool track, bool force)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            Args = ["push", "--progress", "--verbose"];
            if (withTags)
                Args.Add("--tags");
            if (checkSubmodules)
                Args.Add("--recurse-submodules=check");
            if (track)
                Args.Add("-u");
            if (force)
                Args.Add("--force-with-lease");

            Args.Add(remote);
            Args.Add($"{local}:{remoteBranch}");
        }

        public Push(string repo, string remote, string refname, bool isDelete)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            Args = ["push"];
            if (isDelete)
                Args.Add("--delete");
            Args.Add(remote);
            Args.Add(refname);
        }

        public async Task<bool> RunAsync()
        {
            SSHKey = await new Config(WorkingDirectory).GetAsync($"remote.{_remote}.sshkey").ConfigureAwait(false);
            return await ExecAsync().ConfigureAwait(false);
        }

        private readonly string _remote;
    }
}
