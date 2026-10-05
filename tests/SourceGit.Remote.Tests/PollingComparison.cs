using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

// One source is compiled against each revision's existing client/contracts DLLs.
// No product instrumentation is added to either measured revision.
internal static class PollingComparison
{
    internal static async Task RunAsync(string serverDll, string output)
    {
        Directory.CreateDirectory(output);
        output = Path.GetFullPath(output);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = budget.Token;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverDll);
        start.ArgumentList.Add("--record");
        start.ArgumentList.Add(Path.Combine(output, "server.json"));
        start.Environment["SOURCEGIT_DEBUG_LOG"] = "1";
        using var server = Process.Start(start)!;
        using var consumer = Process.GetCurrentProcess();
        var logs = new ConcurrentQueue<string>();
        var stderr = Task.Run(async () => { while (await server.StandardError.ReadLineAsync() is { } line) logs.Enqueue(line); });
        var stdout = server.StandardOutput;
        var samples = new List<Sample>();
        try
        {
            var ready = await stdout.ReadLineAsync(token);
            if (ready?.StartsWith("SOURCEGIT_READY ") != true) throw new Exception("Server failed readiness");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "server.json"), token), RemoteJson.Default.ServerRecord)!;
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token, token);
            var repositoryIds = new List<string>();
            var paths = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(output, "repo-" + i);
                Directory.CreateDirectory(path);
                var init = await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: path), token);
                if (init.ExitCode != 0) throw new Exception("Git init failed");
                paths.Add(path);
            }
            // Warm JIT, process-launch and native cleanup before collecting samples.
            var first = await client.OpenRepositoryAsync(paths[0], token);
            repositoryIds.Add(first.Id);
            await Work(0, -1);
            foreach (var count in new[] { 1, 4 })
            {
                while (repositoryIds.Count < count)
                    repositoryIds.Add((await client.OpenRepositoryAsync(paths[repositoryIds.Count], token)).Id);
                for (var sample = 0; sample < 3; sample++)
                    await Measure("idle", count, sample, async () => { await Task.Delay(TimeSpan.FromSeconds(10), token); return Array.Empty<double>(); });
                for (var sample = 0; sample < 5; sample++)
                {
                    var captured = sample;
                    await Measure("active", count, sample, () => Task.WhenAll(Enumerable.Range(0, count).Select(index => Work(index, captured))));
                }
            }
            await client.ShutdownAsync(token);
            await server.WaitForExitAsync(token);
            await stderr;
            await File.WriteAllLinesAsync(Path.Combine(output, "server-debug.log"), logs, token);
            var result = new
            {
                protocol = Protocol.Version, build = Protocol.Build,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processors = Environment.ProcessorCount,
                stopwatchFrequency = Stopwatch.Frequency,
                transport = "loopback TCP; matching Debug client/server; debug logging enabled for both revisions",
                design = "Three 10-second idle samples and five 1-second command batches at 1 and 4 repositories; one warmup; outer runner uses baseline/current/current/baseline order",
                getOperation = logs.Count(line => line.StartsWith("REQ GetOperation ")),
                waitOperation = logs.Count(line => line.StartsWith("REQ WaitOperation ")),
                samples,
                unmeasured = new[] { "OS scheduler wakeups", "GUI workload", "/proc scans", "authoritative server-terminal to client latency", "local startup and WSL upgrade timing" },
                latencyDefinition = "Same-machine Stopwatch timestamp written by the command immediately before exit to client-visible result, including process-tree/output cleanup; not server-terminal latency"
            };
            await File.WriteAllTextAsync(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"PASS comparison protocol={Protocol.Version} build={Protocol.Build} samples={samples.Count} GetOperation={result.getOperation} WaitOperation={result.waitOperation} output={output}");

            async Task<double> Work(int index, int sample)
            {
                var marker = Path.Combine(paths[index], "finished-" + Interlocked.Increment(ref countMarker) + ".txt");
                var script = "Start-Sleep -Seconds 1; [IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', [Diagnostics.Stopwatch]::GetTimestamp().ToString([Globalization.CultureInfo]::InvariantCulture))";
                var command = "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var execution = await client.ExecuteCustomAsync(new CustomCommandRequest(repositoryIds[index], command, Guid.NewGuid().ToString("N")), token);
                var observed = Stopwatch.GetTimestamp();
                if (execution.ExitCode != 0) throw new Exception("Measured command failed: " + execution.Error);
                var completed = long.Parse(await File.ReadAllTextAsync(marker, token), System.Globalization.CultureInfo.InvariantCulture);
                return (observed - completed) * 1000.0 / Stopwatch.Frequency;
            }
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await stderr;
            await File.WriteAllLinesAsync(Path.Combine(output, "server-debug.log"), logs);
        }

        async Task Measure(string phase, int repositories, int index, Func<Task<double[]>> work)
        {
            server.Refresh(); consumer.Refresh();
            var serverCpu = server.TotalProcessorTime;
            var clientCpu = consumer.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var latencies = await work();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            server.Refresh(); consumer.Refresh();
            samples.Add(new Sample(phase, repositories, index, elapsed,
                (server.TotalProcessorTime - serverCpu).TotalMilliseconds,
                (consumer.TotalProcessorTime - clientCpu).TotalMilliseconds,
                server.Threads.Count, consumer.Threads.Count, server.HandleCount, consumer.HandleCount, latencies));
            Console.WriteLine($"SAMPLE {phase} repositories={repositories} index={index} elapsed_ms={elapsed:F1}");
        }
    }

    private static int countMarker;
    private sealed record Sample(string Phase, int Repositories, int Index, double ElapsedMs, double ServerCpuMs, double ClientCpuMs,
        int ServerThreads, int ClientThreads, int ServerHandles, int ClientHandles, double[] CommandExitToClientMs);
}
