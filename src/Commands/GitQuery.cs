using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Generic git query command for use by the Watcher and other internal components.
    /// Executes a raw git subcommand and returns stdout.
    /// </summary>
    public class GitQuery : Command
    {
        public GitQuery(string repo, string operation, params string[] args)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            Args = new System.Collections.Generic.List<string> { operation };
            Args.AddRange(args);
        }

        public string GetResult()
        {
            var rs = ReadToEnd();
            if (!rs.IsSuccess)
                return string.Empty;
            return rs.StdOut;
        }

        public int GetExitCode()
        {
            var proc = new System.Diagnostics.Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch
            {
                return -1;
            }

            try
            {
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch
            {
                if (!proc.HasExited) proc.Kill();
                proc.WaitForExit();
                return -1;
            }
            finally { proc.Dispose(); }
        }

        public async Task<string> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return string.Empty;
            return rs.StdOut;
        }
    }
}