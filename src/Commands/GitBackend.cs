using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SourceGit.Commands
{
    public class GitCommandSpec
    {
        public string WorkingDirectory { get; init; } = null;
        public Command.EditorType Editor { get; init; } = Command.EditorType.CoreEditor;
        public string SSHKey { get; init; } = string.Empty;
        public string Operation { get; init; } = string.Empty;
        public IReadOnlyList<string> Args { get; init; } = null;
    }

    public abstract class GitBackend
    {
        public abstract ProcessStartInfo CreateStartInfo(GitCommandSpec spec, bool redirect);

        public virtual string NormalizeRepositoryPath(string path, string baseRepositoryPath)
        {
            return path;
        }

        public virtual bool DirectoryExists(string path)
        {
            return System.IO.Directory.Exists(path);
        }

        public virtual bool FileExists(string path)
        {
            return System.IO.File.Exists(path);
        }

        public virtual long GetFileSize(string path)
        {
            return new System.IO.FileInfo(path).Length;
        }

        public virtual string ReadFile(string path)
        {
            return System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
        }

        public virtual System.IO.Stream OpenRead(string path)
        {
            return System.IO.File.OpenRead(path);
        }

        public virtual System.IO.Stream OpenWrite(string path)
        {
            return System.IO.File.Create(path);
        }

        public virtual void WriteFile(string path, string content)
        {
            System.IO.File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        }

        public virtual void WriteFileAndReplace(string path, string content)
        {
            var tmpFile = $"{path}.tmp";
            System.IO.File.WriteAllText(tmpFile, content, System.Text.Encoding.UTF8);
            System.IO.File.Move(tmpFile, path, true);
        }

        public virtual bool DirectoryContainsFile(string directory, string fileName)
        {
            return System.IO.File.Exists(System.IO.Path.Combine(directory, fileName));
        }

        public virtual bool PathExists(string path)
        {
            return System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
        }

        public virtual System.IO.FileInfo[] GetFiles(string directory)
        {
            return new System.IO.DirectoryInfo(directory).GetFiles();
        }

        public virtual void MoveFile(string source, string destination)
        {
            System.IO.File.Move(source, destination, true);
        }

        public virtual void DeleteFile(string path)
        {
            System.IO.File.Delete(path);
        }

        public virtual void DeleteDirectory(string path)
        {
            System.IO.Directory.Delete(path, true);
        }

        protected static ProcessStartInfo CreateCommonStartInfo(GitCommandSpec spec, bool redirect)
        {
            var start = new ProcessStartInfo()
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Force using this app as SSH askpass program.
            var selfExecFile = Environment.ProcessPath;
            start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`.
            start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
            start.Environment.Add("SOURCEGIT_LAUNCH_AS_ASKPASS", "TRUE");
            if (!OperatingSystem.IsLinux())
                start.Environment.Add("DISPLAY", "required");

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(spec.SSHKey))
                start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{spec.SSHKey}' -F '/dev/null'");

            // Force using en_US.UTF-8 locale.
            if (OperatingSystem.IsLinux())
            {
                start.Environment.Add("LANG", "C");
                start.Environment.Add("LC_ALL", "C");
            }

            return start;
        }

        protected static void AppendEditorArgs(StringBuilder builder, Command.EditorType editor, bool useSourceGitEditor)
        {
            var selfExecFile = Environment.ProcessPath;
            switch (editor)
            {
                case Command.EditorType.CoreEditor:
                    if (useSourceGitEditor)
                        builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    else
                        builder.Append("-c core.editor=true ");
                    break;
                case Command.EditorType.RebaseEditor:
                    if (useSourceGitEditor)
                        builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    else
                        builder.Append("-c core.editor=true -c sequence.editor=true -c rebase.abbreviateCommands=true ");
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }
        }

        protected static void AppendEditorArgs(System.Collections.ObjectModel.Collection<string> args, Command.EditorType editor, bool useSourceGitEditor)
        {
            var selfExecFile = Environment.ProcessPath;
            switch (editor)
            {
                case Command.EditorType.CoreEditor:
                    args.Add("-c");
                    args.Add(useSourceGitEditor ? $"core.editor=\"{selfExecFile}\" --core-editor" : "core.editor=true");
                    break;
                case Command.EditorType.RebaseEditor:
                    args.Add("-c");
                    args.Add(useSourceGitEditor ? $"core.editor=\"{selfExecFile}\" --rebase-message-editor" : "core.editor=true");
                    args.Add("-c");
                    args.Add(useSourceGitEditor ? $"sequence.editor=\"{selfExecFile}\" --rebase-todo-editor" : "sequence.editor=true");
                    args.Add("-c");
                    args.Add("rebase.abbreviateCommands=true");
                    break;
                default:
                    args.Add("-c");
                    args.Add("core.editor=true");
                    break;
            }
        }
    }

    public class WindowsGitBackend : GitBackend
    {
        public override ProcessStartInfo CreateStartInfo(GitCommandSpec spec, bool redirect)
        {
            var start = CreateCommonStartInfo(spec, redirect);
            start.FileName = Native.OS.GitExecutable;

            start.ArgumentList.Add("--no-pager");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.quotepath=off");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"credential.helper={Native.OS.CredentialHelper}");
            AppendEditorArgs(start.ArgumentList, spec.Editor, true);
            if (spec.Args is { Count: > 0 })
            {
                foreach (var arg in spec.Args)
                    start.ArgumentList.Add(arg);
            }

            if (!string.IsNullOrEmpty(spec.WorkingDirectory))
                start.WorkingDirectory = spec.WorkingDirectory;

            return start;
        }
    }

    public partial class WslGitBackend : GitBackend
    {
        private readonly Models.WslRepositoryPath _wslPath;

        public WslGitBackend(Models.WslRepositoryPath wslPath)
        {
            _wslPath = wslPath;
        }

        public override ProcessStartInfo CreateStartInfo(GitCommandSpec spec, bool redirect)
        {
            var start = CreateCommonStartInfo(spec, redirect);
            start.FileName = "wsl.exe";

            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(_wslPath.Distro);
            start.ArgumentList.Add("--cd");
            start.ArgumentList.Add(_wslPath.LinuxPath);
            start.ArgumentList.Add("--exec");
            start.ArgumentList.Add("git");
            start.ArgumentList.Add("--no-pager");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.quotepath=off");

            AppendEditorArgs(start.ArgumentList, spec.Editor);

            if (spec.Args is { Count: > 0 })
            {
                foreach (var arg in spec.Args)
                    start.ArgumentList.Add(ConvertArgPathForWsl(arg));
            }

            return start;
        }

        public override string NormalizeRepositoryPath(string path, string baseRepositoryPath)
        {
            return _wslPath.ToWindowsPath(path);
        }

        public override bool DirectoryExists(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return System.IO.Directory.Exists(wslPath.ToNativeWindowsPath());

            return base.DirectoryExists(path);
        }

        public override bool FileExists(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return System.IO.File.Exists(wslPath.ToNativeWindowsPath());

            return base.FileExists(path);
        }

        public override long GetFileSize(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return new System.IO.FileInfo(wslPath.ToNativeWindowsPath()).Length;

            return base.GetFileSize(path);
        }

        public override System.IO.FileInfo[] GetFiles(string directory)
        {
            if (Models.WslRepositoryPath.TryParse(directory, out var wslPath))
            {
                var nativePath = wslPath.ToNativeWindowsPath();
                return new System.IO.DirectoryInfo(nativePath).GetFiles();
            }

            return base.GetFiles(directory);
        }

        public override string ReadFile(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return System.IO.File.ReadAllText(wslPath.ToNativeWindowsPath(), System.Text.Encoding.UTF8);

            return base.ReadFile(path);
        }

        public override System.IO.Stream OpenRead(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return System.IO.File.OpenRead(wslPath.ToNativeWindowsPath());

            return base.OpenRead(path);
        }

        public override System.IO.Stream OpenWrite(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return System.IO.File.Create(wslPath.ToNativeWindowsPath());

            return base.OpenWrite(path);
        }

        public override void WriteFile(string path, string content)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                System.IO.File.WriteAllText(wslPath.ToNativeWindowsPath(), content, System.Text.Encoding.UTF8);
            else
                base.WriteFile(path, content);
        }

        public override void WriteFileAndReplace(string path, string content)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
            {
                var nativePath = wslPath.ToNativeWindowsPath();
                var tmpFile = $"{nativePath}.tmp";
                System.IO.File.WriteAllText(tmpFile, content, System.Text.Encoding.UTF8);
                System.IO.File.Move(tmpFile, nativePath, true);
            }
            else
            {
                base.WriteFileAndReplace(path, content);
            }
        }

        public override void DeleteFile(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
            {
                var startInfo = new ProcessStartInfo()
                {
                    FileName = "wsl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList =
                    {
                        "-d", wslPath.Distro,
                        "--exec",
                        "rm", Models.WslRepositoryPath.ToLinuxPath(path),
                    },
                };
                Process.Start(startInfo).WaitForExit();
            }
            else
            {
                base.DeleteFile(path);
            }
        }

        public override void DeleteDirectory(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
            {
                var startInfo = new ProcessStartInfo()
                {
                    FileName = "wsl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList =
                    {
                        "-d", wslPath.Distro,
                        "--exec",
                        "rm", "-rf", Models.WslRepositoryPath.ToLinuxPath(path),
                    },
                };
                Process.Start(startInfo).WaitForExit();
            }
            else
            {
                base.DeleteDirectory(path);
            }
        }

        private static void AppendEditorArgs(System.Collections.ObjectModel.Collection<string> args, Command.EditorType editor)
        {
            switch (editor)
            {
                case Command.EditorType.RebaseEditor:
                    args.Add("-c");
                    args.Add("core.editor=true");
                    args.Add("-c");
                    args.Add("sequence.editor=true");
                    args.Add("-c");
                    args.Add("rebase.abbreviateCommands=true");
                    break;
                default:
                    args.Add("-c");
                    args.Add("core.editor=true");
                    break;
            }
        }

        private static string ConvertArgPathForWsl(string arg)
        {
            var equals = arg.IndexOf('=');
            if (equals > 0 && equals + 1 < arg.Length)
            {
                var value = arg[(equals + 1)..];
                if (IsWindowsPath(value))
                    return $"{arg[..(equals + 1)]}{Models.WslRepositoryPath.ToLinuxPath(value)}";
            }

            if (IsWindowsPath(arg))
                return Models.WslRepositoryPath.ToLinuxPath(arg);

            return arg;
        }

        private static bool IsWindowsPath(string arg)
        {
            return Models.WslRepositoryPath.TryParse(arg, out _) ||
                (arg.Length > 2 && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/'));
        }
    }
}
