using System.Diagnostics;
using System.Text;
using SourceGit.Commands;
using SourceGit.Models;

internal static class FetchCancellationChecks
{
    internal static async Task RunAsync()
    {
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "git.exe" : "git"))
            .First(File.Exists);
        var original = SourceGit.Native.OS.GitExecutable;
        SourceGit.Native.OS.GitExecutable = git;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var log = new ReadyLog();
            // Runs through the actual local GitProcess adapter. Output is the barrier;
            // no marker polling and no UI renderer is required.
            var script = "[Console]::WriteLine('FETCH_TEST_READY:' + $PID); Start-Sleep -Seconds 60";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var command = new Command
            {
                WorkingDirectory = Path.GetFullPath("."), RaiseError = false,
                Args = "-c \"alias.fetch-test=!powershell.exe -NoProfile -EncodedCommand " + encoded + "\" fetch-test",
                CancellationToken = cancellation.Token, ReapOnCancellation = true, Log = log,
            };
            var execution = command.ExecAsync();
            Process? child = null;
            try
            {
                var pid = await log.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                child = Process.GetProcessById(pid);
                // Keep a stable handle before cancellation so PID reuse is not a test oracle.
                _ = child.SafeHandle;
                cancellation.Cancel();
                var success = await execution.WaitAsync(TimeSpan.FromSeconds(15));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (success || !child.HasExited) throw new InvalidOperationException("Canceled command did not reap/return failure");
                Console.WriteLine("PASS Local auto-fetch command cancellation reaps the actual Git child and returns failure");
            }
            finally
            {
                cancellation.Cancel();
                if (child is { HasExited: false }) child.Kill(true);
                child?.Dispose();
                await execution.WaitAsync(TimeSpan.FromSeconds(15));
            }

            using var alreadyCanceled = new CancellationTokenSource();
            alreadyCanceled.Cancel();
            var notStarted = new Command
            {
                WorkingDirectory = Path.GetFullPath("."), Args = "--version", RaiseError = false,
                CancellationToken = alreadyCanceled.Token, ReapOnCancellation = true,
            };
            if (await notStarted.ExecAsync()) throw new InvalidOperationException("Pre-canceled command reported success");
            Console.WriteLine("PASS Pre-canceled local command returns without accessing an unstarted process");
        }
        finally { SourceGit.Native.OS.GitExecutable = original; }
    }

    private sealed class ReadyLog : ICommandLog
    {
        internal readonly TaskCompletionSource<int> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void AppendLine(string line)
        {
            const string prefix = "FETCH_TEST_READY:";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(line[prefix.Length..], out var pid)) Ready.TrySetResult(pid);
        }
    }
}
