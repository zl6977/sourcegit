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

        public string[] Execute(IReadOnlyList<IReadOnlyList<string>> commands)
        {
            return ExecuteAsync(commands).GetAwaiter().GetResult();
        }

        public async Task<string[]> ExecuteAsync(IReadOnlyList<IReadOnlyList<string>> commands)
        {
            var script = BuildScript(commands);
            _tmpFile = Path.Combine(Path.GetTempPath(), $"sourcegit_wsl_{Guid.NewGuid():N}.sh");
            File.WriteAllText(_tmpFile, script, new System.Text.UTF8Encoding(false));

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
                        WslPathFromWin(_tmpFile),
                    },
                };

                using var proc = new Process { StartInfo = start };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                await proc.WaitForExitAsync().ConfigureAwait(false);

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
            sb.Append("#!/bin/bash\n");
            sb.Append($"cd {_wslPath.LinuxPath}\n");

            for (var i = 0; i < commands.Count; i++)
            {
                sb.Append("git --no-pager -c core.quotepath=off");
                foreach (var arg in commands[i])
                    sb.Append(" ").Append(EscapeBash(arg));
                sb.Append("\n");
                sb.Append("printf '\\0\\0\\0\\0\\0\\0\\0\\0'\n");
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
            if (winPath.Length >= 3 && char.IsLetter(winPath[0]) && winPath[1] == ':')
            {
                var drive = char.ToLowerInvariant(winPath[0]);
                var rest = winPath.Substring(3).Replace('\\', '/');
                return $"/mnt/{drive}/{rest}";
            }

            return winPath.Replace('\\', '/');
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
