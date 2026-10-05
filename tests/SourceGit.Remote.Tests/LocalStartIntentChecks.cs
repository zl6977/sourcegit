using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class LocalStartIntentChecks
{
    internal static async Task OwnerAsync(string id, string stage)
    {
        await using var manager = new ConnectionManager(Options(id));
        async Task Pause(Process child)
        {
            Console.WriteLine("CHILD " + child.Id);
            await Task.Delay(Timeout.Infinite);
        }
        manager.ConfigureLocalLauncher = launcher =>
        {
            if (stage == "unbound") launcher.BeforeChildBound = (child, _) => Pause(child);
            else launcher.AfterProcessStarted = (child, _, _) => Pause(child);
        };
        await manager.ConnectAsync();
    }
    internal static async Task RunAsync()
    {
        await IdentityChecksAsync();
        foreach (var mode in new[] { "timeout", "bound", "unbound" }) await CaseAsync(mode);
    }
    private static async Task IdentityChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-intent-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "instance.json");
        var intents = new LocalStartupIntentFile(path);
        try
        {
            using var self = Process.GetCurrentProcess();
            var created = await intents.BeginAsync(default);
            var bound = await intents.BindAsync(created, self);
            using (var observed = LocalStartupIntentFile.ObserveChild(bound))
                if (observed == null || observed.Id != self.Id) throw new Exception("Stable live child identity was lost");
            if (LocalStartupIntentFile.ObserveChild(bound with { ChildStart = bound.ChildStart - 1 }) != null || self.HasExited)
                throw new Exception("A reused PID identity was followed or signaled");
            await intents.RemoveAsync(bound with { Nonce = Guid.NewGuid().ToString("N") });
            if (await intents.ReadAsync(default) != bound) throw new Exception("Wrong nonce removed a startup intent");
            await intents.RemoveAsync(bound);
            Console.WriteLine("PASS Stable startup identity refuses a reused PID/start, preserves a foreign nonce and never signals the live process");

            foreach (var bytes in new[] { "{bad", new string('x', 16 * 1024 + 1) })
            {
                await File.WriteAllTextAsync(path + ".startup.pending", bytes);
                try { await intents.ReadAsync(default); throw new Exception("Invalid startup intent was accepted"); }
                catch (IOException) { }
                if (await File.ReadAllTextAsync(path + ".startup.pending") != bytes) throw new Exception("Unreadable startup intent was altered");
                File.Delete(path + ".startup.pending");
            }
            Console.WriteLine("PASS Malformed and oversized startup intents remain unchanged with explicit errors");
        }
        finally { Directory.Delete(root, true); }
    }
    private static ConnectionOptions Options(string id) => new(id, "Startup intent fixture", "Local", "", LocalServerPath: Assembly.GetExecutingAssembly().Location);
    private static async Task CaseAsync(string mode)
    {
        var id = "intent-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pipeName = "sourcegit-intent-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var next = new ConnectionManager(Options(id));
        Process? owner = null, child = null;
        Task<RemoteRepositoryClient>? connecting = null;
        var original = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        next.Diagnostic += value => { if (value.Message.StartsWith("Observing pending local startup")) pending.TrySetResult(); };
        next.ConfigureLocalLauncher = launcher =>
        {
            launcher.BeforeProcessStarted = _ => throw new Exception("Startup intent protection attempted a competing process");
            launcher.AfterProcessStarted = (_, _, _) => throw new Exception("Unexpected second process");
        };
        try
        {
            int pid;
            if (mode == "timeout")
            {
                var spawned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Use a direct owned launcher to inject the ready deadline.
                var recordPath = path;
                await using var launcher = new LocalServerLauncher(Assembly.GetExecutingAssembly().Location, recordPath,
                    readyTimeout: TimeSpan.FromMilliseconds(300));
                launcher.AfterProcessStarted = (process, _, _) => { spawned.TrySetResult(process.Id); return Task.CompletedTask; };
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", pipeName);
                var start = launcher.StartAsync((_, _) => throw new Exception("Unexpected upgrade"), token);
                pid = await spawned.Task.WaitAsync(token);
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", original);
                await pipe.WaitForConnectionAsync(token);
                try { await start; throw new Exception("Gated child ignored ready deadline"); }
                catch (IOException error) when (error.Message.Contains("within 0.3 seconds")) { }
                await launcher.DisposeAsync();
            }
            else
            {
                var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--local-intent-owner", id, "--intent-stage", mode }) info.ArgumentList.Add(argument);
                info.Environment["SOURCEGIT_TEST_START_PIPE"] = pipeName;
                owner = Process.Start(info)!;
                _ = owner.SafeHandle;
                var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(token);
                if (line?.StartsWith("CHILD ") != true) throw new Exception("Isolated owner did not register its child");
                pid = int.Parse(line[6..]);
                await pipe.WaitForConnectionAsync(token);
                owner.Kill(); // Kill only the stable owner handle; the server survives.
                await owner.WaitForExitAsync(token);
            }
            child = Process.GetProcessById(pid); _ = child.SafeHandle;
            if (File.Exists(path) || child.HasExited) throw new Exception("Fixture was no longer live before record publication");
            var intents = new LocalStartupIntentFile(path);
            var intent = await intents.ReadAsync(token) ?? throw new Exception("Startup intent disappeared");
            if (mode == "timeout")
            {
                await using var waiter = new LocalServerLauncher(Assembly.GetExecutingAssembly().Location, path, readyTimeout: TimeSpan.FromMilliseconds(300));
                waiter.BeforeProcessStarted = _ => throw new Exception("Timed-out pending wait attempted another child");
                try { await waiter.StartAsync((_, _) => throw new Exception("Unexpected pending upgrade"), token); throw new Exception("Live pending child bypassed wait deadline"); }
                catch (IOException error) when (error.Message.Contains("intent preserved and no successor started")) { }
                if (await intents.ReadAsync(token) != intent || child.HasExited) throw new Exception("Wait deadline removed intent or terminated live child");
                Console.WriteLine("PASS Pending record observation deadline preserves the live child's exact intent and never starts a successor");
            }
            connecting = next.ConnectAsync(token);
            await pending.Task.WaitAsync(token);
            if (mode == "unbound")
            {
                try { await connecting; throw new Exception("Unknown child identity allowed startup"); }
                catch (IOException error) when (error.Message.Contains("identity is unknown")) { }
                if (await intents.ReadAsync(token) != intent || child.HasExited) throw new Exception("Unknown startup intent or child was altered");
            }
            else if (connecting.IsCompleted) throw new Exception("Pending record waiter did not retain its authoritative wait");
            await pipe.WriteAsync(new byte[] { 1 }, token); await pipe.FlushAsync(token);
            if (mode == "unbound")
            {
                // Subscribe before releasing startup, and reread after subscription.
                using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                watcher.Created += (_, _) => published.TrySetResult(); watcher.Renamed += (_, _) => published.TrySetResult();
                watcher.EnableRaisingEvents = true;
                if (!File.Exists(path)) await published.Task.WaitAsync(token);
                connecting = next.ConnectAsync(token);
            }
            var client = await connecting.WaitAsync(token);
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), RemoteJson.Default.ServerRecord)!;
            if (record.ProcessId != pid || record.InstanceId != client.InstanceId) throw new Exception("Pending child was replaced after publication");
            if (mode != "unbound" && await intents.ReadAsync(token) != null) throw new Exception("Authenticated recovered startup did not release its exact intent");
            if (mode == "unbound" && await intents.ReadAsync(token) != intent) throw new Exception("Ambiguous startup identity was automatically removed");
            await next.ShutdownForUpgradeAsync(client, token);
            await child.WaitForExitAsync(token);
            if (mode == "unbound") await intents.RemoveAsync(intent); // Only fixture cleanup after independently confirmed child exit.
            Console.WriteLine("PASS " + mode + " startup: persistent identity prevents a second process; original child survives closed startup pipes, publishes and is authenticated/closed without polling");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", original);
            if (pipe.IsConnected) try { await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync(); } catch (IOException) { }
            if (next.Client is { IsDisconnected: false } client) try { await client.ShutdownAsync(); } catch (IOException) { }
            await next.DisposeAsync();
            if (child != null) { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); child.Dispose(); }
            if (owner != null) { if (!owner.HasExited) owner.Kill(); await owner.WaitForExitAsync(); owner.Dispose(); }
            if (connecting != null) try { await connecting; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
}
