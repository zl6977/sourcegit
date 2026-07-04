using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class SaveRevisionFile
    {
        public static async Task RunAsync(string repo, string revision, string file, string saveTo)
        {
            var dir = System.IO.Path.GetDirectoryName(saveTo) ?? string.Empty;
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var isLFSFiltered = await new IsLFSFiltered(repo, revision, file).GetResultAsync().ConfigureAwait(false);
            if (isLFSFiltered)
            {
                var pointerStream = await QueryFileContent.RunAsync(repo, revision, file).ConfigureAwait(false);
                await ExecCmdAsync(repo, ["lfs", "smudge"], saveTo, pointerStream).ConfigureAwait(false);
            }
            else
            {
                await ExecCmdAsync(repo, ["show", $"{revision}:{file}"], saveTo).ConfigureAwait(false);
            }
        }

        private static async Task ExecCmdAsync(string repo, System.Collections.Generic.List<string> args, string outputFile, Stream input = null)
        {
            var cmd = new Command { WorkingDirectory = repo, Args = args };
            var starter = GitService.CreateStartInfo(cmd, true);
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardInput = true;

            await using (var sw = System.IO.File.Create(outputFile))
            {
                Process proc = null;
                try
                {
                    proc = Process.Start(starter)!;
                    _ = proc.StandardError.ReadToEndAsync();

                    if (input != null)
                    {
                        var inputString = await new System.IO.StreamReader(input).ReadToEndAsync().ConfigureAwait(false);
                        await proc.StandardInput.WriteAsync(inputString).ConfigureAwait(false);
                    }

                    await proc.StandardOutput.BaseStream.CopyToAsync(sw).ConfigureAwait(false);
                    if (!proc.HasExited) proc.Kill();
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (proc != null && !proc.HasExited) proc.Kill();
                    Models.Notification.Send(repo, "Save file failed: " + e.Message, true);
                }
                finally { proc?.Dispose(); }
            }
        }
    }
}
