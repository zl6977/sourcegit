using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Execute multiple git commands in a single WSL session for performance.
    /// Outputs are separated by null-byte delimiters in stdout.
    /// </summary>
    public class WslBatchExecutor : IDisposable
    {
        private const string Delimiter = "\0\0\0\0\0\0\0\0";

        private readonly Models.WslRepositoryPath _wslPath;
        private string _tmpFile;

        public WslBatchExecutor(Models.WslRepositoryPath wslPath)
        {
            _wslPath = wslPath;
        }

        public void Dispose()
        {
            CleanUp();
        }

        public async Task<string[]> ExecuteAsync(IReadOnlyList<IReadOnlyList<string>> commands)
        {
            var script = BuildScript(commands);
            _tmpFile = Path.Combine(Path.GetTempPath(), $"sourcegit_wsl_{Guid.NewGuid():N}.sh");
            File.WriteAllText(_tmpFile, script, Encoding.UTF8);

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    ArgumentList =
                    {
                        "-d", _wslPath.Distro,
                        "--exec", "bash",
                        $"/{WslPathFromWin(_tmpFile)}",
                    },
                };

                using var proc = new Process { StartInfo = start };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.HasExited) proc.Kill();
                await proc.WaitForExitAsync();

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                return SplitOutput(stdout, commands.Count);
            }
            finally
            {
                CleanUp();
            }
        }

        private string BuildScript(IReadOnlyList<IReadOnlyList<string>> commands)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/bash");
            sb.AppendLine($"cd {_wslPath.LinuxPath}");

            for (var i = 0; i < commands.Count; i++)
            {
                sb.Append("git --no-pager -c core.quotepath=off");
                foreach (var arg in commands[i])
                    sb.Append(" ").Append(EscapeBash(arg));
                sb.AppendLine();
                sb.AppendLine("printf '\\0\\0\\0\\0\\0\\0\\0\\0'");
            }

            return sb.ToString();
        }

        private static string[] SplitOutput(string stdout, int count)
        {
            var results = new string[count];
            var parts = stdout.Split(new[] { Delimiter }, StringSplitOptions.None);

            for (var i = 0; i < count && i < parts.Length; i++)
                results[i] = parts[i];

            return results;
        }

        private static string EscapeBash(string arg)
        {
            if (string.IsNullOrEmpty(arg))
                return "''";
            if (arg.Contains('\''))
                return "'" + arg.Replace("'", "'\\''") + "'";
            return "'" + arg + "'";
        }

        private static string WslPathFromWin(string winPath)
        {
            var drive = char.ToLowerInvariant(winPath[0]);
            var rest = winPath.Substring(3).Replace('\\', '/');
            return $"{drive}/{rest}";
        }

        private void CleanUp()
        {
            if (!string.IsNullOrEmpty(_tmpFile))
            {
                try { File.Delete(_tmpFile); } catch { }
                _tmpFile = null;
            }
        }
    }
}