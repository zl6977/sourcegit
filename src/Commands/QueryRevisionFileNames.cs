using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRevisionFileNames : Command
    {
        public QueryRevisionFileNames(string repo, string revision)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = ["ls-tree", "-r", "--name-only", revision];
        }

        public async Task<List<string>> GetResultAsync()
        {
            var outs = new List<string>();

            Process proc = null;

            try
            {
                proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

_ = proc.StandardError.ReadToEndAsync();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 0 } line)
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
