using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class WslComparison
{
    internal static async Task RunAsync(string distro, string package, string output)
    {
        Directory.CreateDirectory(output);
        output = Path.GetFullPath(output);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = budget.Token;
        var samples = new List<object>();
        var bytes = await File.ReadAllBytesAsync(package, token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        bytes[4] ^= 1; // A different immutable version with identical executable contents.
        var alternate = Path.Combine(output, "alternate.tar.gz");
        await File.WriteAllBytesAsync(alternate, bytes, token);
        var alternateHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var type = typeof(RemoteRepositoryClient).Assembly.GetType("SourceGit.RemoteClient.ServerDeployer")!;
        var startMethod = type.GetMethod("StartAsync")!;
        var installMethod = type.GetMethod("InstallAsync")!;
        var nativeBootstrap = startMethod.GetParameters().Length == 2;
        for (var index = -1; index < 5; index++)
        {
            var root = "/tmp/sourcegit-wsl-perf-" + Guid.NewGuid().ToString("N");
            var calls = 0;
            RemoteRepositoryClient? client = null;
            try
            {
                Func<string, byte[]?, CancellationToken, Task<string>> run = Run;
                var original = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [run, root], null)!;
                var upgrade = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [run, root], null)!;
                await (Task)installMethod.Invoke(original, [package, hash, token])!;
                var started = Stopwatch.GetTimestamp();
                var first = await Start(original, null);
                client = await Connect(Record(first));
                var startupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await (Task)installMethod.Invoke(upgrade, [alternate, alternateHash, token])!;
                var discovered = await Start(upgrade, null);
                if (!(bool)discovered.GetType().GetProperty("UpgradeRequired")!.GetValue(discovered)! || Record(discovered).InstanceId != Record(first).InstanceId)
                    throw new Exception("Upgrade installation displaced the running server");
                using var oldExit = await ObserveExit(Record(first).ProcessId, root);
                var callsBefore = calls;
                started = Stopwatch.GetTimestamp();
                await client.ShutdownAsync(token); await client.DisposeAsync(); client = null;
                object next;
                if (nativeBootstrap)
                    next = await Start(upgrade, discovered);
                else
                {
                    // Preserve the baseline production manager's 200ms/StartAsync loop.
                    // Current production passes the old stable identity in a single bootstrap.
                    do { await Task.Delay(200, token); next = await Start(upgrade, null); }
                    while (Record(next).InstanceId == Record(first).InstanceId);
                }
                await oldExit.WaitForExitAsync(token);
                if (oldExit.ExitCode != 0) throw new Exception("Old pidfd observer failed: " + await oldExit.StandardError.ReadToEndAsync(token));
                client = await Connect(Record(next));
                var upgradeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (Record(next).InstanceId == Record(first).InstanceId) throw new Exception("No successor instance");
                var successorCalls = calls - callsBefore;
                using var finalExit = await ObserveExit(Record(next).ProcessId, root);
                await client.ShutdownAsync(token); await client.DisposeAsync(); client = null;
                await finalExit.WaitForExitAsync(token);
                if (finalExit.ExitCode != 0) throw new Exception("Final pidfd observer failed");
                await Run($"test ! -f '{root}/instance-{Protocol.Build}.json' && rm -rf -- '{root}'", null, token);
                if (index >= 0) samples.Add(new { index, startupMs, shutdownToAuthenticatedSuccessorMs = upgradeMs, successorStartScripts = successorCalls });
            }
            finally
            {
                if (client != null)
                {
                    try { await client.ShutdownAsync(token); } catch (IOException) { }
                    await client.DisposeAsync();
                }
            }

            async Task<string> Run(string script, byte[]? input, CancellationToken cancellation)
            {
                if (script.Contains(" bootstrap --record ") || script.Contains("nohup \"$version/sourcegit-server\"")) calls++;
                using var process = Process.Start(Wsl(script))!;
                var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
                var stderr = process.StandardError.ReadToEndAsync(cancellation);
                using var registration = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
                if (input != null) await process.StandardInput.BaseStream.WriteAsync(input, cancellation);
                process.StandardInput.Close(); await process.WaitForExitAsync(cancellation);
                var result = await stdout; var error = await stderr;
                if (process.ExitCode != 0) throw new IOException(error);
                return result;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new
        {
            protocol = Protocol.Version, build = Protocol.Build, packageSha256 = hash, samples,
            design = "WSL x64 actual ServerDeployer in a unique /tmp root per sample; one warmup/five measured; install excluded; baseline production manager loop reproduced, current single bootstrap with old identity; authentication/native exit required",
            unmeasured = new[] { "WSL host CPU", "OS wakeups", "entire ConnectionManager installation/preflight latency" }
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine($"PASS WSL comparison protocol={Protocol.Version} build={Protocol.Build} samples={samples.Count}");

        async Task<object> Start(object deployer, object? previous)
        {
            var task = (Task)startMethod.Invoke(deployer, nativeBootstrap ? [token, previous] : [token])!;
            await task; return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        async Task<RemoteRepositoryClient> Connect(ServerRecord record)
        {
            var connected = new RemoteRepositoryClient();
            try { await connected.ConnectAsync("127.0.0.1", record.Port, record.Token, token); return connected; }
            catch { await connected.DisposeAsync(); throw; }
        }
        async Task<Process> ObserveExit(int pid, string root)
        {
            // Open and validate an owned stable pidfd before sending Shutdown.
            var script = "import os,select; from pathlib import Path; pid=" + pid + "; fd=os.pidfd_open(pid); assert b'" + root + "' in Path(f'/proc/{pid}/cmdline').read_bytes(); print('ARMED',flush=True); p=select.poll(); p.register(fd,select.POLLIN); assert p.poll(240000), 'Native exit observer deadline'; os.close(fd)";
            var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-d", distro, "--exec", "python3", "-c", script }) start.ArgumentList.Add(value);
            var process = Process.Start(start)!;
            if (await process.StandardOutput.ReadLineAsync(token) != "ARMED")
            {
                var error = await process.StandardError.ReadToEndAsync(token); process.Dispose(); throw new IOException("Cannot arm native exit observer: " + error);
            }
            return process;
        }
        ProcessStartInfo Wsl(string script)
        {
            var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-d", distro, "--exec", "sh", "-c", script.Replace("\r\n", "\n") }) start.ArgumentList.Add(value);
            return start;
        }
    }
    private static ServerRecord Record(object value) => (ServerRecord)value.GetType().GetProperty("Record")!.GetValue(value)!;
}
