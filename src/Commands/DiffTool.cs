using Process = SourceGit.Remote.GitProcess;
﻿using System;
using System.Diagnostics;

namespace SourceGit.Commands
{
    public class DiffTool : Command
    {
        public DiffTool(string repo, Models.DiffOption option)
        {
            WorkingDirectory = repo;
            Context = repo;
            _option = option;
        }

        public void Open()
        {
            var tool = Native.OS.GetDiffMergeTool(true);
            if (tool == null)
            {
                RaiseException("Invalid diff/merge tool in preference setting!");
                return;
            }

            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
            {
                _ = OpenRemoteAsync(tool);
                return;
            }

            if (string.IsNullOrEmpty(tool.Cmd))
            {
                if (!CheckGitConfiguration())
                    return;

                Args = $"difftool -g --no-prompt {_option}";
            }
            else
            {
                var cmd = $"{tool.Exec.Quoted()} {tool.Cmd}";
                Args = $"-c difftool.sourcegit.cmd={cmd.Quoted()} difftool --tool=sourcegit --no-prompt {_option}";
            }

            try
            {
                Process.Start(CreateGitStartInfo(false));
            }
            catch (Exception ex)
            {
                RaiseException(ex.Message);
            }
        }

        private async System.Threading.Tasks.Task OpenRemoteAsync(Models.DiffMergeTool tool)
        {
            try
            {
                if (!string.IsNullOrEmpty(tool.Cmd))
                    await SourceGit.Remote.RemoteExternalTools.DiffAsync(WorkingDirectory, _option, tool).ConfigureAwait(false);
                else
                {
                    if (!CheckGitConfiguration()) return;
                    Editor = EditorType.None;
                    Args = $"difftool -g --no-prompt {_option}";
                    await ExecAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error) { RaiseException(error.Message); }
        }

        private bool CheckGitConfiguration()
        {
            var config = new Config(WorkingDirectory).ReadAll();
            if (config.TryGetValue("diff.guitool", out var guiTool))
                return CheckCLIBasedTool(guiTool);
            if (config.TryGetValue("merge.guitool", out var mergeGuiTool))
                return CheckCLIBasedTool(mergeGuiTool);
            if (config.TryGetValue("diff.tool", out var diffTool))
                return CheckCLIBasedTool(diffTool);
            if (config.TryGetValue("merge.tool", out var mergeTool))
                return CheckCLIBasedTool(mergeTool);

            RaiseException("Missing git configuration: diff.guitool");
            return false;
        }

        private bool CheckCLIBasedTool(string tool)
        {
            if (tool.StartsWith("vimdiff", StringComparison.Ordinal) ||
                tool.StartsWith("nvimdiff", StringComparison.Ordinal))
            {
                RaiseException($"CLI based diff tool \"{tool}\" is not supported by this app!");
                return false;
            }

            return true;
        }

        private Models.DiffOption _option;
    }
}
