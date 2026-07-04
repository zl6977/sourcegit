using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCurrentBranchCommitHashes : Command
    {
        public QueryCurrentBranchCommitHashes(string repo, ulong sinceTimestamp)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = ["log", $"--since=@{sinceTimestamp}", "--format=%H"];
        }

        public async Task<HashSet<string>> GetResultAsync()
        {
            var outs = new HashSet<string>();

            Process proc = null;

            try
            {
                proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

_ = proc.StandardError.ReadToEndAsync();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 8 } line)
                    outs.Add(line);

                if (!proc.HasExited) proc.Kill();
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                if (proc != null && !proc.HasExited) proc.Kill();
            }
            finally
            {
                proc?.Dispose();
            }

            return outs;
        }
    }
}
