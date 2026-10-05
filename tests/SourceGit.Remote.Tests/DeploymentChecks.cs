using System.Diagnostics;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

static class DeploymentChecks
{
    public static async Task RunAsync(string distro, string package)
    {
        var root = "/tmp/sourcegit-deployment-tests-" + Guid.NewGuid().ToString("N");
        var alternate = Path.Combine(Path.GetTempPath(), "sourcegit-deployment-test-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellation = deadline.Token;
        var starts = 0;
        async Task<string> RunRemote(string script, byte[]? input, CancellationToken token)
        {
            if (script.Contains(" bootstrap --record ", StringComparison.Ordinal) && !script.Contains("--wait-only", StringComparison.Ordinal)) starts++;
            var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--distribution", distro, "--exec", "sh", "-c", script.Replace("\r\n", "\n") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            using var cancel = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            if (input != null) await process.StandardInput.BaseStream.WriteAsync(input, token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var result = await output; var diagnostics = await error;
            if (process.ExitCode != 0) throw new IOException(diagnostics);
            return result;
        }
        RemoteRepositoryClient? current = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(package, cancellation);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var deployer = new ServerDeployer(RunRemote, root);
            await deployer.InstallAsync(package, hash, cancellation);
            var installed = new ServerDeployer(RunRemote, root);
            Check(await installed.UseInstalledAsync(null, cancellation), "Matching installed package was not discovered without a local archive");
            Check(!await new ServerDeployer(RunRemote, root).UseInstalledAsync(new string('0', 64), cancellation), "Installed discovery ignored a pinned digest");
            var first = await deployer.StartAsync(cancellation);
            Check(!first.UpgradeRequired, "Fresh instance needs an upgrade");
            current = new RemoteRepositoryClient();
            await current.ConnectAsync("127.0.0.1", first.Record.Port, first.Record.Token, cancellation);
            var worktree = root + "/repo";
            await current.FileAsync(new FileRequest("", "mkdir", worktree), cancellation);
            var init = await current.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: worktree), cancellation);
            Check(init.ExitCode == 0, init.Error);
            var repo = await current.OpenRepositoryAsync(worktree, cancellation);
            var operation = await current.CallAsync("ExecuteCustomCommand", new CustomCommandRequest(repo.Id, "sleep 3", Guid.NewGuid().ToString("N")), RemoteJson.Default.CustomCommandRequest, RemoteJson.Default.OperationInfo, cancellation);
            // Changing gzip's timestamp changes the package digest without changing its binaries.
            bytes[4] ^= 1;
            await File.WriteAllBytesAsync(alternate, bytes, cancellation);
            var alternateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var upgrade = new ServerDeployer(RunRemote, root);
            await upgrade.InstallAsync(alternate, alternateHash, cancellation);
            var discovered = await upgrade.StartAsync(cancellation);
            Check(discovered.UpgradeRequired && discovered.Record.InstanceId == first.Record.InstanceId, "Installing an upgrade displaced the running instance");
            var versions = await RunRemote($"find '{root}/versions' -name .package-sha256 | wc -l", null, cancellation);
            Check(versions.Trim() == "2", "Installation replaced the previous package directory");
            Console.WriteLine("PASS WSL immutable deployment retains the running package and discovers an upgrade");
            try { await current.ShutdownAsync(cancellation); throw new Exception("Active operation did not protect shutdown"); }
            catch (IOException) { }
            Check((await deployer.StartAsync(cancellation)).Record.InstanceId == first.Record.InstanceId, "Rejected upgrade restarted the server");
            Console.WriteLine("PASS WSL upgrade refuses to terminate an active operation");
            operation = await current.CallAsync("WaitOperation", new IdRequest(operation.Id), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, cancellation);
            Check(operation.State == "succeeded", "Protected operation failed");
            await current.ShutdownAsync(cancellation);
            await current.DisposeAsync(); current = null;
            var callsBefore = starts;
            var next = await upgrade.StartAsync(cancellation, discovered);
            Check(starts == callsBefore + 1 && next.Record.InstanceId != first.Record.InstanceId, "Upgrade repeated bootstrap or retained old instance");
            Check(!next.UpgradeRequired, "New instance launched the old package");
            current = new RemoteRepositoryClient();
            await current.ConnectAsync("127.0.0.1", next.Record.Port, next.Record.Token, cancellation);
            Console.WriteLine("PASS WSL graceful upgrade uses one bootstrap call and authenticates the notified replacement instance");
            installed = new ServerDeployer(RunRemote, root);
            Check(await installed.UseInstalledAsync(null, cancellation), "Running installed package was not discovered");
            var reused = await installed.StartAsync(cancellation);
            Check(!reused.UpgradeRequired && reused.Record.InstanceId == next.Record.InstanceId, "Missing local archive selected another installed digest and forced an upgrade");
            Console.WriteLine("PASS WSL installed discovery without a local archive preserves the running package and enforces pinned digests");
            await current.ShutdownAsync(cancellation);
            await current.DisposeAsync(); current = null;
            await upgrade.WaitForExitAsync(next, cancellation);
            var restart = new ServerDeployer(RunRemote, root);
            Check(await restart.UseInstalledAsync(null, cancellation), "Stopped installed package was not discovered");
            var restarted = await restart.StartAsync(cancellation);
            current = new RemoteRepositoryClient();
            await current.ConnectAsync("127.0.0.1", restarted.Record.Port, restarted.Record.Token, cancellation);
            await current.ShutdownAsync(cancellation);
            await current.DisposeAsync(); current = null;
            await restart.WaitForExitAsync(restarted, cancellation);
            Console.WriteLine("PASS WSL stopped server starts automatically from the matching installed package without uploading a local archive");
            // This generated root is the only remote directory this test removes.
            await RunRemote($"test ! -f '{root}/instance-{Protocol.Build}.json' && rm -rf -- '{root}'", null, cancellation);
        }
        finally
        {
            if (current != null) { try { await current.ShutdownAsync(); } catch (IOException) { } await current.DisposeAsync(); }
            File.Delete(alternate);
        }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
}
