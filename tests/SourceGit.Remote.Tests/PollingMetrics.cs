using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class PollingMetrics
{
    internal static async Task RunAsync(string serverDll)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "polling-metrics", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "server.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
        start.Environment["SOURCEGIT_DEBUG_LOG"] = "1";
        using var server = Process.Start(start)!;
        var logs = new ConcurrentQueue<string>();
        var stderr = Task.Run(async () => { while (await server.StandardError.ReadLineAsync() is { } line) logs.Enqueue(line); });
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready?.StartsWith("SOURCEGIT_READY ") != true) throw new Exception("Server failed readiness");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath), RemoteJson.Default.ServerRecord)!;
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
            server.Refresh(); var idleCpu = server.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(10));
            server.Refresh();
            Console.WriteLine($"METRIC idle elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} server_cpu_ms={(server.TotalProcessorTime-idleCpu).TotalMilliseconds:F1} server_threads={server.Threads.Count}");
            await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: root));
            var repo = await client.OpenRepositoryAsync(root);
            for (var sample = 0; sample < 3; sample++)
            {
                var queryBefore = logs.Count(x => x.StartsWith("REQ GetOperation "));
                var waitBefore = logs.Count(x => x.StartsWith("REQ WaitOperation "));
                server.Refresh(); var cpu = server.TotalProcessorTime;
                watch.Restart();
                var command = OperatingSystem.IsWindows() ? "powershell -NoProfile -Command Start-Sleep -Seconds 3" : "sleep 3";
                var result = await client.ExecuteCustomAsync(new(repo.Id, command, Guid.NewGuid().ToString("N")));
                if (result.ExitCode != 0) throw new Exception("Metrics command failed");
                server.Refresh();
                Console.WriteLine($"METRIC active sample={sample} elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} server_cpu_ms={(server.TotalProcessorTime-cpu).TotalMilliseconds:F1} get_operation={logs.Count(x => x.StartsWith("REQ GetOperation "))-queryBefore} wait_operation={logs.Count(x => x.StartsWith("REQ WaitOperation "))-waitBefore}");
            }
            Console.WriteLine("UNMEASURED scheduler wakeups, GUI workload, /proc scans, server-completion-to-client latency");
            await client.ShutdownAsync();
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await stderr;
            await File.WriteAllLinesAsync(Path.Combine(root, "server-debug.log"), logs);
            Console.WriteLine("ARTIFACT " + root);
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
        }
    }
}
