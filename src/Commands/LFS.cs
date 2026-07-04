using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class LFS : Command
    {
        public LFS(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> InstallAsync()
        {
            Args = ["lfs", "install", "--local"];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> TrackAsync(string pattern, bool isFilenameMode)
        {
            Args = ["lfs", "track"];
            if (isFilenameMode)
                Args.Add("--filename");
            Args.Add(pattern);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task FetchAsync(string remote)
        {
            Args = ["lfs", "fetch", remote];
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task PullAsync(string remote)
        {
            Args = ["lfs", "pull", remote];
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task PushAsync(string remote)
        {
            Args = ["lfs", "push", remote];
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task PruneAsync()
        {
            Args = ["lfs", "prune"];
            await ExecAsync().ConfigureAwait(false);
        }

        public async Task<List<Models.LFSLock>> GetLocksAsync(string remote)
        {
            Args = ["lfs", "locks", "--json", $"--remote={remote}"];

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                try
                {
                    var locks = JsonSerializer.Deserialize(rs.StdOut, JsonCodeGen.Default.ListLFSLock);
                    return locks;
                }
                catch
                {
                    // Ignore exceptions.
                }
            }

            return [];
        }

        public async Task<bool> LockAsync(string remote, string file)
        {
            Args = ["lfs", "lock", $"--remote={remote}", file];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UnlockAsync(string remote, string file, bool force)
        {
            Args = ["lfs", "unlock", $"--remote={remote}"];
            if (force)
                Args.Add("-f");
            Args.Add(file);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UnlockMultipleAsync(string remote, List<string> files, bool force)
        {
            Args = ["lfs", "unlock", $"--remote={remote}"];
            if (force)
                Args.Add("-f");
            Args.AddRange(files);
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
