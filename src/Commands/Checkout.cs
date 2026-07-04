using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Checkout : Command
    {
        public Checkout(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> BranchAsync(string branch, bool force)
        {
            Args = ["checkout", "--progress"];
            if (force)
                Args.Add("--force");
            Args.Add(branch);

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> BranchAsync(string branch, string basedOn, bool force, bool allowOverwrite)
        {
            Args = ["checkout", "--progress"];
            if (force)
                Args.Add("--force");
            Args.Add(allowOverwrite ? "-B" : "-b");
            Args.Add(branch);
            Args.Add(basedOn);

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> CommitAsync(string commitId, bool force)
        {
            Args = ["checkout"];
            if (force)
                Args.Add("--force");
            Args.Add("--detach");
            Args.Add("--progress");
            Args.Add(commitId);

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UseTheirsAsync(List<string> files)
        {
            Args = ["checkout", "--theirs", "--"];
            Args.AddRange(files);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UseMineAsync(List<string> files)
        {
            Args = ["checkout", "--ours", "--"];
            Args.AddRange(files);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> FileWithRevisionAsync(string file, string revision)
        {
            Args = ["checkout", "--no-overlay", revision, "--", file];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> MultipleFilesWithRevisionAsync(List<string> files, string revision)
        {
            Args = ["checkout", "--no-overlay", revision, "--"];
            Args.AddRange(files);
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
