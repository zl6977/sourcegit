using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class QueryFileContent
    {
        public static async Task<Stream> RunAsync(string repo, string revision, string file)
        {
            var stream = new MemoryStream();
            var start = CreateStartInfo(repo, ["show", $"{revision}:{file}"]);

            Process proc = null;
            try
            {
                proc = Process.Start(start)!;
                await proc.StandardOutput.BaseStream.CopyToAsync(stream).ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                Models.Notification.Send(repo, $"Failed to query file content: {e}", true);
            }
            finally
            {
                proc?.Dispose();
            }

            stream.Position = 0;
            return stream;
        }

        public static async Task<Stream> FromLFSAsync(string repo, string oid, long size)
        {
            var stream = new MemoryStream();
            var start = CreateStartInfo(repo, ["lfs", "smudge"]);
            start.RedirectStandardInput = true;

            Process proc = null;
            try
            {
                proc = Process.Start(start)!;
                await proc.StandardInput.WriteLineAsync("version https://git-lfs.github.com/spec/v1").ConfigureAwait(false);
                await proc.StandardInput.WriteLineAsync($"oid sha256:{oid}").ConfigureAwait(false);
                await proc.StandardInput.WriteLineAsync($"size {size}").ConfigureAwait(false);
                await proc.StandardOutput.BaseStream.CopyToAsync(stream).ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                Models.Notification.Send(repo, $"Failed to query file content: {e}", true);
            }
            finally
            {
                proc?.Dispose();
            }

            stream.Position = 0;
            return stream;
        }

        private static ProcessStartInfo CreateStartInfo(string repo, System.Collections.Generic.List<string> args)
        {
            var cmd = new Command
            {
                WorkingDirectory = repo,
                Args = args,
            };
            var start = GitService.CreateStartInfo(cmd, true);
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardError = false;
            return start;
        }
    }
}
