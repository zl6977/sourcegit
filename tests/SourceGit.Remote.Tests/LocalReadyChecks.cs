using System.Diagnostics;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class LocalReadyChecks
{
    internal static async Task ChildAsync(string path, string mode)
    {
        var record = new ServerRecord(12345, "isolated-ready-fixture", "fixture-instance", Environment.ProcessId);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
        if (mode == "mismatch") Console.WriteLine("SOURCEGIT_READY wrong-instance");
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var variable = "SOURCEGIT_TEST_READY_MODE";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            foreach (var mode in new[] { "mismatch", "missing", "dispose" })
            {
                Environment.SetEnvironmentVariable(variable, mode);
                var path = Path.Combine(root, mode + ".json");
                var pid = 0;
                LocalServerLauncher? launcher = null;
                try
                {
                    launcher = new LocalServerLauncher(typeof(LocalReadyChecks).Assembly.Location, path, message =>
                    {
                        if (!message.StartsWith("Started local server PID")) return;
                        pid = int.Parse(message.Split(' ')[^1]);
                        if (mode == "dispose") launcher!.Dispose();
                    }, readyTimeout: TimeSpan.FromSeconds(2));
                    try { await launcher.StartAsync((_, _) => throw new Exception("Unexpected upgrade"), default); throw new Exception("Invalid ready succeeded"); }
                    catch (OperationCanceledException) when (mode == "dispose") { }
                    catch (IOException error) when (mode != "dispose")
                    {
                        var expected = mode == "mismatch" ? "does not match" : "within 2 seconds";
                        if (!error.Message.Contains(expected) || !File.Exists(path)) throw;
                        var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), RemoteJson.Default.ServerRecord)!;
                        if (record.ProcessId != pid) throw new Exception("Invalid fixture PID");
                    }
                    if (mode == "dispose")
                    {
                        try { await launcher.StartAsync((_, _) => Task.CompletedTask, default); throw new Exception("Disposed launcher accepted start"); }
                        catch (ObjectDisposedException) { }
                    }
                    Console.WriteLine("PASS Local ready " + mode + ": identity/deadline/disposal result is explicit without accepting a bare record");
                }
                finally
                {
                    launcher?.Dispose();
                    if (pid != 0)
                    {
                        try { using var child = Process.GetProcessById(pid); child.Kill(true); await child.WaitForExitAsync(); }
                        catch (ArgumentException) { }
                    }
                }
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, original); }
    }
}
