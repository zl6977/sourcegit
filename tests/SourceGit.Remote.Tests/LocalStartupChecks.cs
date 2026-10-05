using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class LocalStartupChecks
{
    internal static async Task IncompatibleAsync(string executable)
    {
        var id = "incompatible-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
        using var launcher = new LocalServerLauncher(executable, path);
        var record = await launcher.StartAsync(NoUpgrade, CancellationToken.None);
        try
        {
            Check(record.Build != Protocol.Build, "Supply a server genuinely built with a different SourceGitBuildId");
            await using var manager = new ConnectionManager(new(id, "Incompatible build", "Local", "", LocalServerPath: executable));
            var starts = 0;
            manager.Diagnostic += diagnostic => { if (diagnostic.Message.StartsWith("Started local server PID")) starts++; };
            var error = await ExpectAsync<IOException>(() => manager.ConnectAsync());
            Check(error.Message.Contains("protocol") && starts == 0 && IsAlive(record.ProcessId) && await ReadAsync(path) == record,
                "Incompatible build was replaced or its record was deleted");
            Check(manager.Client == null && manager.State == "Failed", "Incompatible handshake left a connected client");
            Pass("A genuinely different build rejects safe handshake; Local preserves its live process/record and requests manual intervention");
        }
        finally
        {
            // Current protocol cannot safely shut down this isolated old-build fixture.
            // It has accepted no operations; only this test's recorded child is reaped.
            if (IsAlive(record.ProcessId)) { using var process = Process.GetProcessById(record.ProcessId); process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    internal static async Task ChildAsync(string executable, string recordPath, string report, string gate, string release)
    {
        await WaitAsync(() => File.Exists(gate));
        using var launcher = new LocalServerLauncher(executable, recordPath, Console.WriteLine);
        var record = await launcher.StartAsync(NoUpgrade, CancellationToken.None);
        await File.WriteAllTextAsync(report, record.InstanceId + "|" + record.ProcessId);
        await WaitAsync(() => File.Exists(release), 60);
    }

    internal static async Task HoldLockChildAsync(string executable, string path)
    {
        using var held = new FileStream(path + ".startup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { executable, "--record", path }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!;
        _ = server.StandardError.ReadToEndAsync();
        Check((await server.StandardOutput.ReadLineAsync())?.StartsWith("SOURCEGIT_READY ") == true, "Slow holder server failed ready");
        Console.WriteLine("LOCK_HELD");
        await Console.In.ReadLineAsync(); // Parent releases a real OS lock, not an in-process semaphore.
        held.Dispose();
        await Console.In.ReadLineAsync();
    }

    internal static async Task RunAsync(string executable)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "local-startup-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var records = new List<ServerRecord>();
        var children = new List<Process>();
        var release = Path.Combine(root, "release");
        try
        {
            // Separate managers share the real GUI ownership identity but not _connectGate.
            var id = "startup-" + Guid.NewGuid().ToString("N");
            await using (var first = new ConnectionManager(new(id, "first", "Local", "", LocalServerPath: executable)))
            await using (var second = new ConnectionManager(new(id, "second", "Local", "", LocalServerPath: executable)))
            {
                var starts = 0;
                void Count(ConnectionDiagnostic value) { if (value.Message.StartsWith("Started local server PID")) Interlocked.Increment(ref starts); }
                first.Diagnostic += Count; second.Diagnostic += Count;
                var connections = await Task.WhenAll(first.ConnectAsync(), second.ConnectAsync());
                Check(connections[0].InstanceId == connections[1].InstanceId && starts == 1, "Managers spawned multiple local instances");
                var recordPath = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
                records.Add(await ReadAsync(recordPath));
                await first.ShutdownForUpgradeAsync(connections[0]);
            }
            Pass("Concurrent managers execute one actual startup and reuse the same instance");

            var crossRecord = Path.Combine(root, "cross-process.json");
            var gate = Path.Combine(root, "gate");
            var reports = new[] { Path.Combine(root, "child-1"), Path.Combine(root, "child-2") };
            var outputs = new List<Task<string>>();
            for (var i = 0; i < 2; i++)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--local-discover-child", "--server", executable, "--record", crossRecord, "--report", reports[i], "--gate", gate, "--release", release }) start.ArgumentList.Add(arg);
                var child = Process.Start(start) ?? throw new IOException("Discovery child failed to start");
                children.Add(child);
                outputs.Add(child.StandardOutput.ReadToEndAsync());
                _ = child.StandardError.ReadToEndAsync();
            }
            File.WriteAllText(gate, "go");
            await WaitAsync(() => reports.All(File.Exists));
            Check(await File.ReadAllTextAsync(reports[0]) == await File.ReadAllTextAsync(reports[1]), "Separate processes did not discover the same instance");
            var cross = await ReadAsync(crossRecord);
            records.Add(cross);
            await StopAsync(cross);
            File.WriteAllText(release, "go");
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(10));
            Check(children.All(child => child.ExitCode == 0), "Cross-process discovery failed");
            Check((await Task.WhenAll(outputs)).Sum(output => output.Split('\n').Count(line => line.StartsWith("Started local server PID"))) == 1, "Cross-process decision lock permitted more than one actual startup");
            Pass("Separate discovery processes execute one actual startup under the shared decision lock");

            var slowRecord = Path.Combine(root, "slow-holder.json");
            var holderStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--local-hold-lock-child", "--server", executable, "--record", slowRecord }) holderStart.ArgumentList.Add(arg);
            var holder = Process.Start(holderStart)!;
            children.Add(holder);
            _ = holder.StandardError.ReadToEndAsync();
            Check(await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "LOCK_HELD", "Slow holder failed startup");
            var winner = await ReadAsync(slowRecord); records.Add(winner);
            using (var waiting = new LocalServerLauncher(executable, slowRecord))
            {
                var elapsed = Stopwatch.StartNew();
                var discovery = waiting.StartAsync(NoUpgrade, CancellationToken.None);
                // This real delay proves compatibility beyond the former 15s budget.
                await Task.Delay(TimeSpan.FromSeconds(16));
                Check(!discovery.IsCompleted, "Waiter bypassed the held decision lock");
                await holder.StandardInput.WriteLineAsync("release");
                await holder.StandardInput.FlushAsync();
                Check(await discovery.WaitAsync(TimeSpan.FromSeconds(10)) == winner && elapsed.Elapsed > TimeSpan.FromSeconds(15), "Slow decision holder caused a competing instance or timed out");
            }
            await StopAsync(winner);
            await holder.StandardInput.WriteLineAsync("exit");
            await holder.StandardInput.FlushAsync();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(holder.ExitCode == 0, "Slow holder child failed");
            Pass("Real cross-process decision lock held beyond 15 seconds reuses the winning server within the default 60-second budget");

            var fakePath = Path.Combine(root, "probe.json");
            var alive = new ServerRecord(12345, "not-a-secret-fixture", "alive", Environment.ProcessId);
            await WriteAsync(fakePath, alive);
            var probes = 0;
            using (var retry = new LocalServerLauncher(executable, fakePath, probe: (_, _) =>
            {
                if (Interlocked.Increment(ref probes) == 1) throw new IOException("injected transient probe failure");
                return Task.FromResult(Handshake(alive));
            }))
                Check(await retry.StartAsync(NoUpgrade, CancellationToken.None) == alive && probes == 2, "Transient probe failure replaced a live process");
            Pass("First probe failure followed by success reuses the live record");

            probes = 0;
            using (var failed = new LocalServerLauncher(executable, fakePath, probe: (_, _) => { probes++; throw new IOException("injected continuous failure"); }))
            {
                var error = await ExpectAsync<IOException>(() => failed.StartAsync(NoUpgrade, CancellationToken.None));
                Check(probes == 3 && error.Message.Contains(alive.ProcessId.ToString()) && error.Message.Contains("12345") && error.Message.Contains("IOException"), "Exhausted probe retry lacks diagnostics");
                Check(await ReadAsync(fakePath) == alive, "Live record was removed after exhausted probes");
            }
            Pass("Three failed probes preserve a live PID record and report PID/port/exception type");

            var updated = alive with { InstanceId = "updated" };
            probes = 0;
            using (var rediscover = new LocalServerLauncher(executable, fakePath, probe: async (record, _) =>
            {
                if (++probes == 1) await WriteAsync(fakePath, updated);
                return Handshake(record);
            }))
                Check(await rediscover.StartAsync(NoUpgrade, CancellationToken.None) == updated && probes == 2, "Changed record was not rediscovered after a successful old probe");
            Pass("Record replacement during probing is rediscovered without deleting the new record");

            using (var mismatched = new LocalServerLauncher(executable, fakePath, probe: (_, _) => Task.FromResult(Handshake(alive))))
                await ExpectAsync<IOException>(() => mismatched.StartAsync(NoUpgrade, CancellationToken.None));
            Check(await ReadAsync(fakePath) == updated, "Probe instance mismatch deleted the record");
            Pass("Unchanged record with a mismatched probe fails bounded discovery and preserves the record");

            var cancellationPath = Path.Combine(root, "cancel.json");
            using (var held = new FileStream(cancellationPath + ".startup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var waiting = new LocalServerLauncher(executable, cancellationPath))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                var elapsed = Stopwatch.StartNew();
                await ExpectAsync<OperationCanceledException>(() => waiting.StartAsync(NoUpgrade, cancellation.Token));
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(2) && !File.Exists(cancellationPath), "Cancelled lock wait launched a child or returned late");
            }
            Pass("Startup decision lock wait is promptly cancellable without starting a child");

            using (var held = new FileStream(cancellationPath + ".startup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var waiting = new LocalServerLauncher(executable, cancellationPath, lockTimeout: TimeSpan.FromMilliseconds(500)))
            {
                var elapsed = Stopwatch.StartNew();
                var error = await ExpectAsync<IOException>(() => waiting.StartAsync(NoUpgrade, CancellationToken.None));
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(2) && error.Message.Contains("0.5 seconds") && !File.Exists(cancellationPath), "Startup lock wait exceeded its finite deadline");
            }
            Pass("Startup decision lock contention has an injectable finite deadline (production default 60 seconds)");

            var stallPid = 0;
            var previousStall = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_STALL_STARTUP");
            try
            {
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_STALL_STARTUP", "1");
                using var stalled = new LocalServerLauncher(Assembly.GetExecutingAssembly().Location, Path.Combine(root, "stalled.json"),
                    message => { if (message.StartsWith("Started local server PID")) stallPid = int.Parse(message.Split(' ')[^1]); });
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var elapsed = Stopwatch.StartNew();
                await ExpectAsync<OperationCanceledException>(() => stalled.StartAsync(NoUpgrade, cancellation.Token));
                Check(stallPid != 0 && elapsed.Elapsed < TimeSpan.FromSeconds(2), "Cancellation of an in-progress child startup returned late");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_STALL_STARTUP", previousStall);
                if (stallPid != 0 && IsAlive(stallPid)) { using var process = Process.GetProcessById(stallPid); process.Kill(true); await process.WaitForExitAsync(); }
            }
            Pass("Waiting for an already started child to publish its record is promptly cancellable");

            var invalidPath = Path.Combine(root, "invalid.json");
            File.WriteAllText(invalidPath, "not json");
            using (var invalid = new LocalServerLauncher(executable, invalidPath))
                await ExpectAsync<InvalidDataException>(() => invalid.StartAsync(NoUpgrade, CancellationToken.None));
            Check(File.ReadAllText(invalidPath) == "not json", "Client deleted an unreadable instance record");
            Pass("Malformed client instance record is preserved and never triggers a replacement process");

            var conflictPath = Path.Combine(root, "conflict.json");
            using (var held = new FileStream(conflictPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var competing = new LocalServerLauncher(executable, conflictPath, readyTimeout: TimeSpan.FromMilliseconds(300)))
            {
                var elapsed = Stopwatch.StartNew();
                var error = await ExpectAsync<IOException>(() => competing.StartAsync(NoUpgrade, CancellationToken.None));
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(2) && error.Message.Contains("lifetime lock is held") && error.Message.Contains("no successor started"), "Lifetime-lock conflict lost its bounded publication diagnostic");
                Check(!File.Exists(conflictPath), "Competing child published an instance record despite the lifetime lock");
            }
            Pass("Manual lifetime-lock contention waits on bounded record notification without starting a competing child");

            var stalePath = Path.Combine(root, "stale.json");
            await WriteAsync(stalePath, alive with { ProcessId = cross.ProcessId }); // known reaped test PID
            using (var stale = new LocalServerLauncher(executable, stalePath))
            {
                var successor = await stale.StartAsync(NoUpgrade, CancellationToken.None);
                records.Add(successor);
                Check(successor.InstanceId != alive.InstanceId, "Confirmed dead record did not permit startup");
                await StopAsync(successor);
            }
            Pass("Confirmed dead record is rechecked and replaced by a single new server");

            await UpgradeChecksAsync(executable, root, records);
            await RecordOwnershipChecksAsync(executable, root, records);
        }
        finally
        {
            File.WriteAllText(release, "go");
            foreach (var child in children)
            {
                if (!child.HasExited) child.Kill(true);
                await child.WaitForExitAsync(); child.Dispose();
            }
            // Every PID below was obtained from this invocation's isolated fixture records.
            foreach (var record in records)
                if (IsAlive(record.ProcessId)) { using var process = Process.GetProcessById(record.ProcessId); process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task UpgradeChecksAsync(string executable, string root, List<ServerRecord> records)
    {
        var id = "upgrade-" + Guid.NewGuid().ToString("N");
        await using var manager = new ConnectionManager(new(id, "Upgrade checks", "Local", "", LocalServerPath: executable));
        var changes = new List<bool>();
        var starts = 0;
        ServerRecord? original = null;
        manager.Connected += (_, changed) => changes.Add(changed);
        manager.Diagnostic += diagnostic =>
        {
            if (!diagnostic.Message.StartsWith("Started local server PID")) return;
            starts++;
            if (original != null) Check(!IsAlive(original.ProcessId), "Successor started before the old server exited");
        };
        var client = await manager.ConnectAsync();
        var recordPath = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
        original = await ReadAsync(recordPath); records.Add(original);
        var repositoryPath = Path.Combine(root, "upgrade-repo");
        await client.FileAsync(new FileRequest("", "mkdir", repositoryPath));
        Check((await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: repositoryPath))).ExitCode == 0, "Upgrade repository init failed");
        var repository = await client.OpenRepositoryAsync(repositoryPath);
        foreach (var setting in new[] { new[] { "config", "user.name", "Startup Test" }, new[] { "config", "user.email", "startup@example.invalid" } })
            Check((await client.ExecuteGitAsync(new GitRequest(repository.Id, setting, false))).ExitCode == 0, "Upgrade repository config failed");
        var interaction = new TaskCompletionSource<InteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.InteractionRequested += request => interaction.TrySetResult(request);
        var commit = client.ExecuteGitAsync(new GitRequest(repository.Id, ["commit", "--allow-empty"], false));
        var waiting = await interaction.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Simulate obsolete record metadata while retaining a handshake-compatible
        // live server: exercises the authenticated upgrade branch, not protocol bypass.
        await WriteAsync(recordPath, original with { Build = "obsolete-record-build" });
        try
        {
            await ExpectAsync<IOException>(() => manager.ConnectAsync());
            Check(manager.Client == client && manager.State == "Connected" && starts == 1 && IsAlive(original.ProcessId), "Active upgrade changed the instance/client");
            Check((await client.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Refused-upgrade client stopped working");
        }
        finally { await client.SubmitInteractionAsync(new InteractionReply(waiting.Id, true, "finish upgrade fixture\n")); }
        Check((await commit.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode == 0, "Active operation did not finish");
        Pass("Authenticated Local upgrade refuses an active operation and preserves the old client/record");
        manager.StateChanged += state => { if (state == "Starting") Check(manager.Client == null, "Upgrade gap exposed old client"); };
        var replacement = await manager.ConnectAsync();
        var replacementRecord = await ReadAsync(recordPath); records.Add(replacementRecord);
        Check(replacement.InstanceId != original.InstanceId && starts == 2 && changes.SequenceEqual(new[] { true, true }), "Upgrade did not replace the server or lost instance-change notification");
        await manager.ShutdownForUpgradeAsync(replacement);
        Pass("Local successor starts only after accepted Shutdown and confirmed exit; Connected reports the changed instance");

        var failureId = "upgrade-failure-" + Guid.NewGuid().ToString("N");
        var failurePath = Path.Combine(Path.GetTempPath(), "sourcegit-server", failureId, "instance.json");
        using var initial = new LocalServerLauncher(executable, failurePath);
        var old = await initial.StartAsync(NoUpgrade, CancellationToken.None); records.Add(old);
        await using var failing = new ConnectionManager(new(failureId, "Failed successor", "Local", "", LocalServerPath: Path.Combine(root, "missing-server.dll")));
        await failing.ConnectAsync();
        await WriteAsync(failurePath, old with { Build = "obsolete-record-build" });
        await ExpectAsync<IOException>(() => failing.ConnectAsync());
        Check(failing.Client == null && failing.State == "Failed" && !IsAlive(old.ProcessId), "Failed successor left a disposed client or false Connected state");
        Pass("Failed startup after accepted Local upgrade leaves Client null and State Failed");
    }

    private static async Task RecordOwnershipChecksAsync(string executable, string root, List<ServerRecord> records)
    {
        foreach (var malformed in new[] { false, true })
        {
            var path = Path.Combine(root, malformed ? "malformed.json" : "foreign.json");
            using var launcher = new LocalServerLauncher(executable, path);
            var record = await launcher.StartAsync(NoUpgrade, CancellationToken.None); records.Add(record);
            var content = malformed ? "not json" : JsonSerializer.Serialize(record with { InstanceId = "externally-replaced", ProcessId = Environment.ProcessId }, RemoteJson.Default.ServerRecord);
            await File.WriteAllTextAsync(path, content);
            await StopAsync(record);
            Check(await File.ReadAllTextAsync(path) == content, "Server exit removed an unowned/unreadable record");
        }
        Pass("Server exit preserves externally replaced and unreadable instance records");
    }

    private static Task NoUpgrade(ServerRecord _, CancellationToken __) => throw new Exception("Unexpected upgrade");
    private static HandshakeResult Handshake(ServerRecord record) => new(record.InstanceId, Protocol.Version, Protocol.Build, []);
    private static async Task<ServerRecord> ReadAsync(string path) => JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), RemoteJson.Default.ServerRecord)!;
    private static async Task WriteAsync(string path, ServerRecord record)
    {
        var temporary = path + ".test-tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
        File.Move(temporary, path, true);
    }
    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task StopAsync(ServerRecord record)
    {
        await using var client = new RemoteRepositoryClient();
        await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
        await client.ShutdownAsync();
        await WaitAsync(() => !IsAlive(record.ProcessId));
    }
    private static async Task WaitAsync(Func<bool> ready, int seconds = 15)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Fixture wait timed out");
            await Task.Delay(50);
        }
    }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pass(string message) => Console.WriteLine("PASS " + message);
}
