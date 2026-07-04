using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Worktree : Command
    {
        public Worktree(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<List<Models.Worktree>> ReadAllAsync()
        {
            Args = ["worktree", "list", "--porcelain"];

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            var worktrees = new List<Models.Worktree>();
            Models.Worktree last = null;
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    {
                        var fullPath = GitService.NormalizeRepositoryPath(line.Substring(9).Trim(), WorkingDirectory);
                        last = new Models.Worktree() { FullPath = fullPath };
                        worktrees.Add(last);
                        continue;
                    }

                    if (last == null)
                        continue;

                    if (line.StartsWith("bare", StringComparison.Ordinal))
                        last.IsBare = true;
                    else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                        last.Head = line.Substring(5).Trim();
                    else if (line.StartsWith("branch ", StringComparison.Ordinal))
                        last.Branch = line.Substring(7).Trim();
                    else if (line.StartsWith("detached", StringComparison.Ordinal))
                        last.IsDetached = true;
                    else if (line.StartsWith("locked", StringComparison.Ordinal))
                        last.IsLocked = true;
                }
            }

            return worktrees;
        }

        public async Task<bool> AddAsync(string fullpath, string name, bool createNew, string tracking)
        {
            Args = ["worktree", "add"];
            if (!string.IsNullOrEmpty(tracking))
                Args.Add("--track");
            if (!string.IsNullOrEmpty(name))
            {
                Args.Add(createNew ? "-b" : "-B");
                Args.Add(name);
            }
            Args.Add(fullpath);

            if (!string.IsNullOrEmpty(tracking))
                Args.Add(tracking);
            else if (!string.IsNullOrEmpty(name) && !createNew)
                Args.Add(name);

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> PruneAsync()
        {
            Args = ["worktree", "prune", "-v"];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> LockAsync(string fullpath)
        {
            Args = ["worktree", "lock", fullpath];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UnlockAsync(string fullpath)
        {
            Args = ["worktree", "unlock", fullpath];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> RemoveAsync(string fullpath, bool force)
        {
            Args = ["worktree", "remove"];
            if (force)
                Args.Add("-f");
            Args.Add(fullpath);
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
