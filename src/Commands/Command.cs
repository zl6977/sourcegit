using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public List<string> Args { get; set; } = null;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            Log?.AppendLine($"$ git {GetLogArgs()}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            var captured = new CapturedProcess() { Process = proc };
            var capturedLock = new object();
            try
            {
                proc.Start();

                // Not safe, please only use `CancellationToken` in readonly commands.
                if (CancellationToken.CanBeCanceled)
                {
                    CancellationToken.Register(() =>
                    {
                        lock (capturedLock)
                        {
                            if (captured is { Process: { HasExited: false } })
                                captured.Process.Kill();
                        }
                    });
                }
            }
            catch (Exception e)
            {
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // After cancellation (Kill() was called), wait for the process to actually
                // exit so the async reader threads (BeginOutputReadLine/BeginErrorReadLine)
                // can drain and terminate cleanly before the Process is disposed.
                if (!proc.HasExited)
                {
                    try { proc.WaitForExit(5000); } catch { /* ignore */ }
                }
                HandleOutput(e.Message, errs);
            }

            lock (capturedLock)
            {
                captured.Process = null;
            }

            Log?.AppendLine(string.Empty);

            if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
            {
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        RaiseException(errMsg);
                }

                return false;
            }

            return true;
        }

        protected Result ReadToEnd()
        {
            var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            try
            {
                var rs = new Result() { IsSuccess = true };
                var stdOutTask = proc.StandardOutput.ReadToEndAsync();
                var stdErrTask = proc.StandardError.ReadToEndAsync();
                Task.WaitAll(stdOutTask, stdErrTask);
                proc.WaitForExit();
                rs.StdOut = stdOutTask.Result;
                rs.StdErr = stdErrTask.Result;

                rs.IsSuccess = proc.ExitCode == 0;
                return rs;
            }
            catch
            {
                if (!proc.HasExited) proc.Kill();
                proc.WaitForExit();
                return Result.Failed("Process error");
            }
            finally { proc.Dispose(); }
        }

        protected async Task<Result> ReadToEndAsync()
        {
            var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            try
            {
                var rs = new Result() { IsSuccess = true };
                var stdOutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
                var stdErrTask = proc.StandardError.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
                rs.StdOut = await stdOutTask;
                rs.StdErr = await stdErrTask;
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);

                rs.IsSuccess = proc.ExitCode == 0;
                return rs;
            }
            catch (OperationCanceledException)
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    try { proc.WaitForExit(5000); } catch { /* ignore */ }
                }
                return Result.Failed("Cancelled");
            }
            catch
            {
                if (!proc.HasExited) proc.Kill();
                proc.WaitForExit();
                return Result.Failed("Process error");
            }
            finally { proc.Dispose(); }
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            return GitService.CreateStartInfo(this, redirect);
        }

        private string GetLogArgs()
        {
            if (Args is { Count: > 0 })
            {
                var parts = new List<string>();
                foreach (var arg in Args)
                    parts.Add(arg.Quoted());
                return string.Join(" ", parts);
            }

            return string.Empty;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
