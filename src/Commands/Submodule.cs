using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Submodule : Command
    {
        public Submodule(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> AddAsync(string url, string relativePath, bool recursive)
        {
            Args = ["-c", "protocol.file.allow=always", "submodule", "add", url, relativePath];

            var succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            Args = ["submodule", "update", "--init"];
            if (recursive)
                Args.Add("--recursive");
            Args.Add("--");
            Args.Add(relativePath);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetURLAsync(string path, string url)
        {
            Args = ["submodule", "set-url", "--", path, url];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetBranchAsync(string path, string branch)
        {
            if (string.IsNullOrEmpty(branch))
                Args = ["submodule", "set-branch", "-d", "--", path];
            else
                Args = ["submodule", "set-branch", "-b", branch, "--", path];

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateAsync(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            Args = ["submodule", "update"];

            if (init)
                Args.Add("--init");
            if (recursive)
                Args.Add("--recursive");
            if (useRemote)
                Args.Add("--remote");
            if (modules.Count > 0)
            {
                Args.Add("--");
                Args.AddRange(modules);
            }

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeinitAsync(string module, bool force)
        {
            Args = ["submodule", "deinit"];
            if (force)
                Args.Add("-f");
            Args.Add("--");
            Args.Add(module);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync(string module)
        {
            Args = ["rm", "-rf", module];
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
