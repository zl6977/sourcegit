using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Stash : Command
    {
        public Stash(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> PushAsync(string message, bool includeUntracked = true, bool keepIndex = false)
        {
            Args = ["stash", "push"];
            if (includeUntracked)
                Args.Add("--include-untracked");
            if (keepIndex)
                Args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                Args.Add("-m");
                Args.Add(message);
            }

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> PushAsync(string message, List<Models.Change> changes, bool keepIndex)
        {
            Args = ["stash", "push", "--include-untracked"];
            if (keepIndex)
                Args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                Args.Add("-m");
                Args.Add(message);
            }

            Args.Add("--");
            foreach (var c in changes)
                Args.Add(c.Path);

            return await ExecAsync().ConfigureAwait(false);
        }


        public async Task<bool> PushOnlyStagedAsync(string message, bool keepIndex)
        {
            Args = ["stash", "push", "--staged"];
            if (keepIndex)
                Args.Add("--keep-index");
            if (!string.IsNullOrEmpty(message))
            {
                Args.Add("-m");
                Args.Add(message);
            }
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> ApplyAsync(string name, bool restoreIndex)
        {
            Args = ["stash", "apply", "-q"];
            if (restoreIndex)
                Args.Add("--index");
            Args.Add(name);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> CheckoutBranchAsync(string name, string branch)
        {
            Args = ["stash", "branch", branch, name];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> PopAsync(string name)
        {
            Args = ["stash", "pop", "-q", "--index", name];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DropAsync(string name)
        {
            Args = ["stash", "drop", "-q", name];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> ClearAsync()
        {
            Args = ["stash", "clear"];
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
