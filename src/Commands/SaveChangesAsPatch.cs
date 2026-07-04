using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class SaveChangesAsPatch
    {
        public static async Task<bool> ProcessLocalChangesAsync(string repo, List<Models.Change> changes, bool isUnstaged, string saveTo)
        {
            await using (var sw = File.Create(saveTo))
            {
                foreach (var change in changes)
                {
                    if (!await ProcessSingleChangeAsync(repo, new Models.DiffOption(change, isUnstaged), sw))
                        return false;
                }
            }

            return true;
        }

        public static async Task<bool> ProcessRevisionCompareChangesAsync(string repo, List<Models.Change> changes, string baseRevision, string targetRevision, string saveTo)
        {
            await using (var sw = File.Create(saveTo))
            {
                foreach (var change in changes)
                {
                    if (!await ProcessSingleChangeAsync(repo, new Models.DiffOption(baseRevision, targetRevision, change), sw))
                        return false;
                }
            }
            return true;
        }

        public static async Task<bool> ProcessStashChangesAsync(string repo, List<Models.DiffOption> opts, string saveTo)
        {
            await using (var sw = File.Create(saveTo))
            {
                foreach (var opt in opts)
                {
                    if (!await ProcessSingleChangeAsync(repo, opt, sw))
                        return false;
                }
            }
            return true;
        }

        private static async Task<bool> ProcessSingleChangeAsync(string repo, Models.DiffOption opt, Stream writer)
        {
            var args = new List<string> { "diff", "--no-color", "--no-ext-diff", "--ignore-cr-at-eol", "--unified=4" };
            args.AddRange(opt.ToArgs());
            var cmd = new Command { WorkingDirectory = repo, Args = args };
            var starter = GitService.CreateStartInfo(cmd, true);
            starter.WindowStyle = ProcessWindowStyle.Hidden;

            Process proc = null;
            try
            {
                proc = Process.Start(starter)!;
                _ = proc.StandardError.ReadToEndAsync();
                await proc.StandardOutput.BaseStream.CopyToAsync(writer).ConfigureAwait(false);
                if (!proc.HasExited) proc.Kill();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
            catch (Exception e)
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                Models.Notification.Send(repo, "Save change to patch failed: " + e.Message, true);
                return false;
            }
            finally { proc?.Dispose(); }
        }
    }
}
