using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class UpdateIndexInfo
    {
        public UpdateIndexInfo(string repo, List<Models.Change> changes)
        {
            _repo = repo;

            foreach (var c in changes)
            {
                if (c.Index == Models.ChangeState.Renamed)
                {
                    _patchBuilder.Append("0 0000000000000000000000000000000000000000\t");
                    _patchBuilder.Append(c.Path);
                    _patchBuilder.Append("\n100644 ");
                    _patchBuilder.Append(c.DataForAmend.ObjectHash);
                    _patchBuilder.Append("\t");
                    _patchBuilder.Append(c.OriginalPath);
                }
                else if (c.Index == Models.ChangeState.Added)
                {
                    _patchBuilder.Append("0 0000000000000000000000000000000000000000\t");
                    _patchBuilder.Append(c.Path);
                }
                else if (c.Index == Models.ChangeState.Deleted)
                {
                    _patchBuilder.Append("100644 ");
                    _patchBuilder.Append(c.DataForAmend.ObjectHash);
                    _patchBuilder.Append("\t");
                    _patchBuilder.Append(c.Path);
                }
                else
                {
                    _patchBuilder.Append(c.DataForAmend.FileMode);
                    _patchBuilder.Append(" ");
                    _patchBuilder.Append(c.DataForAmend.ObjectHash);
                    _patchBuilder.Append("\t");
                    _patchBuilder.Append(c.Path);
                }

                _patchBuilder.Append('\n');
            }
        }

        public async Task<bool> ExecAsync()
        {
            var cmd = new Command { WorkingDirectory = _repo, Args = ["-c", "core.editor=true", "update-index", "--index-info"] };
            var starter = GitService.CreateStartInfo(cmd, true);
            starter.RedirectStandardInput = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;

            Process proc = null;
            try
            {
                proc = Process.Start(starter)!;
                await proc.StandardInput.WriteAsync(_patchBuilder.ToString());
                proc.StandardInput.Close();

                var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var rs = proc.ExitCode == 0;

                if (!rs)
                    Models.Notification.Send(_repo, err, true);

                return rs;
            }
            catch (Exception e)
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                Models.Notification.Send(_repo, "Failed to update index: " + e.Message, true);
                return false;
            }
            finally
            {
                proc?.Dispose();
            }
        }

        private readonly string _repo;
        private readonly StringBuilder _patchBuilder = new();
    }
}
