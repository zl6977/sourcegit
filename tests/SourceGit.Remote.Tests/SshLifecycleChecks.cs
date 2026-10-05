using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class SshLifecycleChecks
{
    internal static async Task RunAsync(string config, string package, string home, string user, bool cleanupOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run real Windows OpenSSH checks against the isolated Linux sshd fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var bytes = await File.ReadAllBytesAsync(package, token);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var options = new ConnectionOptions(Guid.NewGuid().ToString("N"), "Isolated SSH fixture", "Ssh", "sourcegit-fixture", user,
            PackagePath: package, PackageSha256: digest, SshOptions: "-F " + config.Replace('\\', '/'));
        var manager = new ConnectionManager(options);
        var alternate = Path.Combine(Path.GetDirectoryName(package)!, "alternate.tar.gz");
        ConnectionManager? replacement = null;
        RemoteRepositoryClient? cleanup = null;
        ServerDeployment? deployment = null;
        try
        {
            var client = await manager.ConnectAsync(token);
            cleanup = client;
            if (cleanupOnly)
            {
                await client.ShutdownAsync(token);
                cleanup = null;
                Console.WriteLine("PASS Authenticated cleanup of the isolated SSH fixture server");
                return;
            }
            var environment = await client.GetEnvironmentAsync(token);
            Check(environment.Home == home && manager.State == "Connected", "SSH fixture did not isolate HOME or install a usable client");
            var firstInstance = client.InstanceId;
            Check(await Starts() == 1, "Initial SSH connection repeated bootstrap");
            Console.WriteLine("PASS Real Windows OpenSSH authenticates a pinned host key, installs through SSH and opens the production ConnectionManager forwarding channel with an isolated HOME");

            var path = home + "/repository";
            await client.FileAsync(new FileRequest("", "mkdir", path), token);
            Check((await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: path), token)).ExitCode == 0, "SSH fixture Git init failed");
            var repository = await client.OpenRepositoryAsync(path, token);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource<RemoteRepositoryClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Connected += (connected, changed) => { if (connected != client) recovered.TrySetResult(connected); };
            var execution = client.ExecuteCustomAsync(new CustomCommandRequest(repository.Id,
                "printf 'SSH_WRITE_STARTED\\n' >&2; sleep 3; printf 'once\\n' >> write-count", Guid.NewGuid().ToString("N")), token,
                progress => { if (progress.Text.Contains("SSH_WRITE_STARTED", StringComparison.Ordinal)) started.TrySetResult(); });
            if (await Task.WhenAny(started.Task, execution).WaitAsync(token) == execution && !started.Task.IsCompleted)
            {
                var early = await execution;
                throw new Exception("SSH write finished before its stderr start barrier: " + early.ExitCode + " " + early.Error);
            }
            await started.Task.WaitAsync(token);
            try { await manager.ShutdownForUpgradeAsync(client, token); throw new Exception("SSH active write allowed upgrade shutdown"); }
            catch (IOException) { }
            Check(manager.Client == client && manager.State == "Connected", "Rejected SSH shutdown lost its usable channel");
            Console.WriteLine("PASS Authenticated upgrade shutdown over real SSH refuses an active persistent write and retains its client");

            var tunnel = (Process)typeof(ConnectionManager).GetField("_tunnel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            using (var observed = Process.GetProcessById(tunnel.Id))
            {
                _ = observed.SafeHandle;
                observed.Kill(true);
                await observed.WaitForExitAsync(token);
            }
            var connectedAgain = await recovered.Task.WaitAsync(token);
            cleanup = connectedAgain;
            Check(connectedAgain.InstanceId == firstInstance, "Tunnel loss replaced the independent server");
            var result = await execution.WaitAsync(token);
            Check(result.ExitCode == 0, "Write did not recover across SSH tunnel loss");
            var content = await connectedAgain.FileAsync(new FileRequest(repository.Id, "read", path + "/write-count"), token);
            Check(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64!)) == "once\n" && await Starts() == 2,
                "SSH reconnect replayed a write or repeated bootstrap within one recovery");
            Console.WriteLine("PASS Killing the real SSH forwarding process recovers the same server through one bootstrap; the registered write completes exactly once without polling/replay");
            await manager.DisposeAsync();
            cleanup = null;

            // A new gzip header digest selects another immutable directory with the same matched binaries.
            bytes[4] ^= 1; await File.WriteAllBytesAsync(alternate, bytes, token);
            var alternateDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            replacement = new ConnectionManager(options with { PackagePath = alternate, PackageSha256 = alternateDigest });
            var next = await replacement.ConnectAsync(token);
            cleanup = next;
            Check(next.InstanceId != firstInstance && await Starts() == 4, "SSH graceful upgrade did not use one discovery and one exit/ready bootstrap");
            Check((await next.GetEnvironmentAsync(token)).Home == home, "SSH replacement changed its fixture HOME");
            Console.WriteLine("PASS Real SSH graceful immutable upgrade authenticates a different instance with one successor bootstrap and no repeated startup loop");

            var recordText = await RunSsh("cat \"$HOME/.sourcegit-server/instance-" + Protocol.Build + ".json\"", null, token);
            var record = JsonSerializer.Deserialize(recordText, RemoteJson.Default.ServerRecord)!;
            var identity = (await RunSsh("python3 -c 'import pathlib,sys; s=pathlib.Path(\"/proc/\"+sys.argv[1]+\"/stat\").read_text(); print(s[s.rfind(\")\")+2:].split()[19])' " + record.ProcessId, null, token)).Trim();
            deployment = new ServerDeployment(record, false, identity);
            var deployer = new ServerDeployer(RunSsh);
            await deployer.InstallAsync(alternate, alternateDigest, token);
            cleanup = next;
            await next.ShutdownAsync(token); cleanup = null;
            await replacement.DisposeAsync();
            await deployer.WaitForExitAsync(deployment, token);
            Check((await RunSsh("test ! -f \"$HOME/.sourcegit-server/instance-" + Protocol.Build + ".json\" && printf EXITED", null, token)).Trim() == "EXITED",
                "SSH shutdown returned before native exit/record cleanup");
            Console.WriteLine("PASS Final real SSH shutdown acknowledges only accepted closure and native pidfd exit confirms the fixture server/record are gone");
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (cleanup != null) try { await cleanup.ShutdownAsync(cleanupDeadline.Token); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { Console.WriteLine("Fixture cleanup could not acknowledge shutdown: " + error.GetType().Name); }
            await manager.DisposeAsync();
            if (replacement != null) await replacement.DisposeAsync();
            File.Delete(alternate);
        }

        async Task<int> Starts() => int.Parse((await RunSsh("wc -l < \"$HOME/start-calls\"", null, token)).Trim());
        async Task<string> RunSsh(string script, byte[]? input, CancellationToken cancellation)
        {
            var start = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-F", config, "-T", "sourcegit-fixture", "sh -c '" + script.Replace("'", "'\"'\"'") + "'" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var cancel = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(cancellation); var error = process.StandardError.ReadToEndAsync(cancellation);
            if (input != null) await process.StandardInput.BaseStream.WriteAsync(input, cancellation);
            process.StandardInput.Close(); await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0) throw new IOException(await error);
            return await output;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
