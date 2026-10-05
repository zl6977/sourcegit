using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class LifecycleComparison
{
    internal static async Task RunAsync(string serverDll, string executable, string output)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This comparison uses Windows Local startup and Job containment");
        Directory.CreateDirectory(output);
        output = Path.GetFullPath(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var samples = new List<object>();
        var close = typeof(ConnectionManager).GetMethod("ShutdownForUpgradeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // The same real Local manager API, using a unique record namespace per launch.
        for (var index = -1; index < 5; index++)
        {
            var id = "polling-comparison-" + Guid.NewGuid().ToString("N");
            await using var manager = new ConnectionManager(new ConnectionOptions(id, "Isolated performance fixture", "Local", "", LocalServerPath: executable));
            var started = Stopwatch.GetTimestamp();
            var client = await manager.ConnectAsync(token);
            var connectedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var recordPath = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath, token), RemoteJson.Default.ServerRecord)!;
            using var process = Process.GetProcessById(record.ProcessId);
            started = Stopwatch.GetTimestamp();
            await (Task)close.Invoke(manager, [client, token])!;
            await process.WaitForExitAsync(token);
            var closedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (File.Exists(recordPath)) throw new Exception("Local fixture left its owned record after exit");
            if (index >= 0) samples.Add(new { phase = "local-startup", index, connectedMs, shutdownToNativeExitMs = closedMs });
        }

        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--record"); start.ArgumentList.Add(Path.Combine(output, "server.json"));
        using var server = Process.Start(start)!;
        var stderr = server.StandardError.ReadToEndAsync(token);
        try
        {
            if (!(await server.StandardOutput.ReadLineAsync(token))!.StartsWith("SOURCEGIT_READY ")) throw new Exception("Missing readiness");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(output, "server.json"), token), RemoteJson.Default.ServerRecord)!;
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token, token);
            if ((await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: output), token)).ExitCode != 0) throw new Exception("Git init failed");
            var repository = await client.OpenRepositoryAsync(output, token);
            for (var index = -1; index < 5; index++)
            {
                var marker = Path.Combine(output, "descendant-" + index + ".txt");
                var child = "Start-Sleep -Seconds 1; [IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', [Diagnostics.Stopwatch]::GetTimestamp().ToString([Globalization.CultureInfo]::InvariantCulture))";
                var parent = "$s=[Diagnostics.ProcessStartInfo]::new('powershell'); $s.Arguments='-NoProfile -NonInteractive -EncodedCommand " + Encode(child) + "'; $s.UseShellExecute=$false; $s.CreateNoWindow=$true; $s.RedirectStandardOutput=$true; $s.RedirectStandardError=$true; $s.RedirectStandardInput=$true; $p=[Diagnostics.Process]::Start($s); $p.StandardInput.Close(); $p.Dispose()";
                server.Refresh(); var cpu = server.TotalProcessorTime;
                var started = Stopwatch.GetTimestamp();
                var progress = new StringBuilder();
                var result = await client.ExecuteCustomAsync(new CustomCommandRequest(repository.Id, "powershell -NoProfile -NonInteractive -EncodedCommand " + Encode(parent), Guid.NewGuid().ToString("N")), token, update => progress.Append(update.Text));
                var observed = Stopwatch.GetTimestamp();
                server.Refresh();
                if (result.ExitCode != 0) throw new Exception("Tree fixture failed: exit=" + result.ExitCode + " " + result.Error + " " + progress);
                if (!File.Exists(marker)) throw new Exception("Result escaped before its private-pipe descendant finished");
                var finished = long.Parse(await File.ReadAllTextAsync(marker, token), System.Globalization.CultureInfo.InvariantCulture);
                if (index >= 0) samples.Add(new
                {
                    phase = "private-pipe-descendant", index,
                    elapsedMs = Stopwatch.GetElapsedTime(started, observed).TotalMilliseconds,
                    serverCpuMs = (server.TotalProcessorTime - cpu).TotalMilliseconds,
                    descendantPreExitToClientMs = (observed - finished) * 1000.0 / Stopwatch.Frequency
                });
            }
            await client.ShutdownAsync(token); await server.WaitForExitAsync(token);
            await File.WriteAllTextAsync(Path.Combine(output, "server-stderr.log"), await stderr, token);
            await File.WriteAllTextAsync(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new
            {
                protocol = Protocol.Version, build = Protocol.Build, samples,
                design = "One warmup and five measured Local starts/shutdowns, followed by one warmup and five private-pipe descendants; no injected delays in Local startup",
                latencyDefinition = "Descendant writes a Stopwatch marker before exit; latency includes its exit, native or periodic tree cleanup, and RPC result delivery. Not authoritative server-terminal latency."
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"PASS lifecycle comparison protocol={Protocol.Version} build={Protocol.Build} samples={samples.Count}");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(output, "server-stderr.log"), await stderr);
        }
    }
    private static string Encode(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
}
