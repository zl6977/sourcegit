using System.Diagnostics;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;
using SourceGit.Server;

internal static class BootstrapReadyChecks
{
    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run ready primitives on Linux");
        var basePath = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable))))!;
        var recordPath = Path.Combine(basePath, "instance-test-" + Guid.NewGuid().ToString("N") + ".json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        string[] OwnedEndpoints(string path) => Directory.GetDirectories(Path.GetTempPath(), Bootstrap.EndpointPrefix(path) + "*");
        var before = OwnedEndpoints(recordPath).ToHashSet();
        ServerRecord? live = null;
        try
        {
            var first = await Run(executable, recordPath, token);
            live = first.Record;
            Check(!first.UpgradeRequired && live.ProcessId > 0, "Initial bootstrap failed");
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", live.Port, live.Token, token);
            var reused = await Run(executable, recordPath, token);
            Check(reused.Record.InstanceId == live.InstanceId, "Second bootstrap started another server");
            Check(OwnedEndpoints(recordPath).All(before.Contains), "Ready endpoint leaked after success");
            Console.WriteLine("PASS Detached bootstrap returns one ready result, preserves the server after transport EOF and reuses its instance");
            await client.ShutdownAsync(token);
            // This creates a non-child stable native wait after the authenticated shutdown;
            // an already-exited process is covered by the primitive registration test.
            var next = await Run(executable, recordPath, token, live.ProcessId, first.Start);
            Check(next.Record.InstanceId != live.InstanceId && !next.UpgradeRequired, "Exit notification failed to select successor");
            live = next.Record;
            await using var replacement = new RemoteRepositoryClient();
            await replacement.ConnectAsync("127.0.0.1", live.Port, live.Token, token);
            await replacement.ShutdownAsync(token);
            live = null;
            Console.WriteLine("PASS One bootstrap call waits for authenticated old-instance exit and returns the ready successor");
        }
        finally
        {
            if (live != null)
            {
                await using var cleanup = new RemoteRepositoryClient();
                try { await cleanup.ConnectAsync("127.0.0.1", live.Port, live.Token, token); await cleanup.ShutdownAsync(token); }
                catch (IOException) { }
            }
        }

        var failureRoot = Path.GetFullPath(Path.Combine("artifacts", "bootstrap-failed", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(failureRoot);
        // The server publishes its record, then cannot install the helper file.
        Directory.CreateDirectory(Path.Combine(failureRoot, "git-helper-" + Protocol.Build + ".sh"));
        var failureRecord = Path.Combine(failureRoot, "server.json");
        using var failed = Start(executable, "bootstrap", "--record", failureRecord);
        failed.StandardInput.Close();
        var output = failed.StandardOutput.ReadToEndAsync(token);
        var error = failed.StandardError.ReadToEndAsync(token);
        await failed.WaitForExitAsync(token);
        Check(failed.ExitCode != 0 && (await error).Contains("exited before ready", StringComparison.Ordinal), "Early exit did not produce startup diagnostic");
        Check(!(await output).Contains("SOURCEGIT_RECORD", StringComparison.Ordinal), "Bare record was accepted as ready");
        Check(OwnedEndpoints(failureRecord).Length == 0, "Early exit leaked ready endpoint");
        Console.WriteLine("PASS Child failure after record publication rejects bare record and reclaims the ready endpoint");

        using var independent = Start("sh", "-c", "read value");
        var identity = LinuxProcessExit.StartIdentity(independent.Id);
        using var interrupted = Start(executable, "bootstrap", "--record", recordPath, "--wait-only", "--old-pid", independent.Id.ToString(), "--old-start", identity);
        interrupted.StandardInput.Close();
        var registered = await interrupted.StandardOutput.ReadLineAsync(token);
        Check(registered == "SOURCEGIT_EXIT_WAIT_REGISTERED", "Native wait did not register before hangup barrier");
        using (var signal = Start("kill", "-HUP", interrupted.Id.ToString()))
        {
            signal.StandardInput.Close(); await signal.WaitForExitAsync(token);
            Check(signal.ExitCode == 0, "Could not deliver bootstrap hangup");
        }
        await interrupted.WaitForExitAsync(token);
        Check(interrupted.ExitCode != 0 && !independent.HasExited, "Hangup killed independent process or accepted canceled wait");
        independent.StandardInput.WriteLine("exit"); independent.StandardInput.Close();
        await independent.WaitForExitAsync(token);
        Check(OwnedEndpoints(recordPath).All(before.Contains), "Hangup leaked endpoint");
        Console.WriteLine("PASS Hangup after native-wait registration cancels bootstrap without killing the independent process or leaking endpoints");
    }

    private static async Task<(ServerRecord Record, bool UpgradeRequired, string Start)> Run(string executable, string record, CancellationToken token, int? oldPid = null, string? oldStart = null)
    {
        var arguments = new List<string> { "bootstrap", "--record", record };
        if (oldPid != null) arguments.AddRange(["--old-pid", oldPid.Value.ToString(), "--old-start", oldStart!]);
        using var process = Start(executable, arguments.ToArray());
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token); var diagnostics = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var value = await output;
        if (process.ExitCode != 0) throw new IOException(await diagnostics);
        var lines = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ready = lines.Single(line => line.StartsWith("SOURCEGIT_RECORD ", StringComparison.Ordinal));
        return (JsonSerializer.Deserialize(ready[17..], RemoteJson.Default.ServerRecord)!, lines.Contains("SOURCEGIT_UPGRADE_REQUIRED"),
            lines.Single(line => line.StartsWith("SOURCEGIT_PROCESS_START ", StringComparison.Ordinal))[24..]);
    }
    private static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start bootstrap fixture");
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
