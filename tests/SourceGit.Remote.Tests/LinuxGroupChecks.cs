using System.Diagnostics;
using System.Net.Sockets;
using SourceGit.Core;
using SourceGit.Contracts;
using SourceGit.Server;

internal static class LinuxGroupChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native group checks require Linux");
        foreach (var detail in new[] { "closed", "pipes", "branch", "zombie" })
        {
            using var endpoint = new Endpoint();
            using var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, detail)))!;
            using var lifetime = ProcessLifetime.Attach(root);
            using var cancel = new CancellationTokenSource();
            var sockets = new List<Socket>();
            Task? waiting = null;
            try
            {
                Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Root ready barrier missing");
                var early = lifetime.CreateLinuxWaiter();
                try { await early.WaitAsync(); throw new Exception("Waiter attempted to own live managed root"); }
                catch (InvalidOperationException) { }
                root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
                var child = await ReadChild(root);
                var control = await endpoint.Listener.AcceptAsync().WaitAsync(Limit); sockets.Add(control);
                var packet = new byte[4]; await Read(control, packet);
                Check(BitConverter.ToInt32(packet) == child, "Descendant socket identity mismatch");
                root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
                await root.WaitForExitAsync().WaitAsync(Limit);
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var grandchildSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiter = lifetime.CreateLinuxWaiter(ids =>
                {
                    if (ids.Contains(child)) registered.TrySetResult();
                    if (ids.Any(pid => pid != child)) grandchildSeen.TrySetResult();
                });
                waiting = waiter.WaitAsync(cancel.Token);
                await registered.Task.WaitAsync(Limit);
                if (detail == "zombie") await grandchildSeen.Task.WaitAsync(Limit);
                var scans = waiter.SnapshotQueries;
                await Task.Delay(250);
                Check(!waiting.IsCompleted && waiter.SnapshotQueries == scans, "Live orphan was scanned periodically or ignored");
                if (detail == "branch")
                {
                    await control.SendAsync(new byte[] { 1 });
                    var grand = await endpoint.Listener.AcceptAsync().WaitAsync(Limit); sockets.Add(grand);
                    await Read(grand, packet);
                    await grandchildSeen.Task.WaitAsync(Limit);
                    Check(!waiting.IsCompleted, "Parent exit lost late-created grandchild");
                    await grand.SendAsync(new byte[] { 1 });
                }
                else await control.SendAsync(new byte[] { 1 });
                await waiting.WaitAsync(Limit);
                Check(lifetime.IsEmpty && waiter.Reaped > 0 && root.ExitCode == 37, "Stable wait failed to reap descendants or stole .NET root exit code");
                if (detail == "zombie") Check(waiter.Reaped == 2, "Live-parent zombie was not adopted and reaped with its parent");
                await root.StandardOutput.ReadToEndAsync().WaitAsync(Limit);
                await root.StandardError.ReadToEndAsync().WaitAsync(Limit);
                Console.WriteLine($"PASS Linux {detail} orphan waits on stable pidfds without periodic scans and preserves the managed root exit code");
            }
            finally
            {
                cancel.Cancel();
                foreach (var socket in sockets) socket.Dispose();
                lifetime.Terminate(); await root.WaitForExitAsync().WaitAsync(Limit);
                if (waiting != null) try { await waiting.WaitAsync(Limit); } catch (OperationCanceledException) { }
                await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
            }
        }

        using (var endpoint = new Endpoint())
        using (var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, "closed")))!)
        using (var lifetime = ProcessLifetime.Attach(root))
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Cancellation root barrier missing");
            root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
            var child = await ReadChild(root);
            using var control = await endpoint.Listener.AcceptAsync().WaitAsync(Limit);
            await Read(control, new byte[4]);
            root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
            await root.WaitForExitAsync().WaitAsync(Limit);
            try
            {
                var before = Directory.GetFiles("/proc/self/fd").Length;
                var failed = lifetime.CreateLinuxWaiter(notificationDescriptor: _ => int.MaxValue);
                try { await failed.WaitAsync().WaitAsync(Limit); throw new Exception("Native invalid-descriptor notification was ignored"); }
                catch (IOException error) { Check(error.Message.Contains("notification descriptor"), "Native poll failure lost its diagnostic"); }
                Check(!lifetime.IsEmpty && Directory.GetFiles("/proc/self/fd").Length <= before + 4,
                    "Failed native notification killed/released the group or leaked registered descriptors");
                Console.WriteLine("PASS Actual poll invalid-descriptor failure closes owned pidfds/pipes while preserving live group ownership for later cleanup");
                for (var index = 0; index < 20; index++)
                {
                    using var cancel = new CancellationTokenSource();
                    var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var waiter = lifetime.CreateLinuxWaiter(_ => registered.TrySetResult());
                    var wait = waiter.WaitAsync(cancel.Token);
                    await registered.Task.WaitAsync(Limit); cancel.Cancel();
                    try { await wait.WaitAsync(Limit); throw new Exception("Canceled native group wait succeeded"); } catch (OperationCanceledException) { }
                }
                Check(Directory.GetFiles("/proc/self/fd").Length <= before + 4 && Directory.Exists($"/proc/{child}"), "Cancellation leaked descriptors or killed the orphan");
                await ResourceCapacityAsync(lifetime);
                await control.SendAsync(new byte[] { 1 });
                await lifetime.CreateLinuxWaiter().WaitAsync().WaitAsync(Limit);
                Check(lifetime.IsEmpty, "Cleanup after canceled wait failed");
                Console.WriteLine("PASS Twenty canceled group waits reclaim pidfds/pipes without killing descendants; subsequent cleanup reaps them");
            }
            finally { lifetime.Terminate(); await lifetime.WaitForCleanupAsync().WaitAsync(Limit); }
        }
        await ConcurrentAsync();
        await PipelineAsync();
        await MemberBudgetAsync();
        await NativeRegistrationFaultsAsync();
        using (var endpoint = new Endpoint())
        using (var pendingRoot = Process.Start(Info("root", endpoint.Path, "closed"))!)
        using (var pendingLifetime = ProcessLifetime.Attach(pendingRoot))
        {
            Check(await pendingRoot.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Pre-group root not ready");
            try
            {
                Check(GetGroup(pendingRoot.Id) != pendingRoot.Id && !pendingLifetime.IsEmpty,
                    "Live root with no owned group was declared cleaned up");
                pendingLifetime.Terminate();
                await pendingRoot.WaitForExitAsync().WaitAsync(Limit);
                await pendingLifetime.WaitForCleanupAsync().WaitAsync(Limit);
                Check(pendingLifetime.IsEmpty, "Terminated pre-group root retained cleanup ownership");
                Console.WriteLine("PASS Live root before group establishment is not empty; cancellation fallback terminates the root then confirms cleanup");
            }
            finally { if (!pendingRoot.HasExited) pendingLifetime.Terminate(); await pendingRoot.WaitForExitAsync().WaitAsync(Limit); }
        }

        using (var endpoint = new Endpoint())
        using (var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, "closed")))!)
        using (var lifetime = ProcessLifetime.Attach(root))
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Already-empty barrier missing");
            try
            {
                // A forged/reused group leader must not be mistaken for a previously reaped root.
                var foreign = new LinuxGroupExitWaiter(root.Id, () => true);
                try { await foreign.WaitAsync().WaitAsync(Limit); throw new Exception("Live replacement group was accepted as old descendants"); }
                catch (IOException error) { Check(error.Message.Contains("root PID"), "Root identity guard lost its diagnostic"); }
                Check(!root.HasExited, "Rejected identity killed or reaped the live managed root");
                root.StandardInput.Close(); await root.WaitForExitAsync().WaitAsync(Limit);
                var empty = lifetime.CreateLinuxWaiter();
                await empty.WaitAsync().WaitAsync(Limit);
                Check(empty.SnapshotQueries == 0 && empty.Reaped == 0 && root.ExitCode == 0, "Already-reaped empty group scanned /proc or stole a managed status");
                Console.WriteLine("PASS Root identity guard rejects a live replacement without killing/reaping it; already-empty group needs no /proc snapshot or waiting thread");
            }
            finally { if (!root.HasExited) lifetime.Terminate(); await root.WaitForExitAsync().WaitAsync(Limit); }
        }
    }

    private static async Task ResourceCapacityAsync(ProcessLifetime lifetime)
    {
        var before = Directory.GetFiles("/proc/self/fd").Length;
        using var cancel = new CancellationTokenSource();
        var registered = new List<Task>();
        var pending = new List<Task>();
        try
        {
            for (var index = 0; index < 64; index++)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                registered.Add(ready.Task);
                pending.Add(lifetime.CreateLinuxWaiter(_ => ready.TrySetResult()).WaitAsync(cancel.Token));
            }
            await Task.WhenAll(registered).WaitAsync(Limit);
            var peak = Directory.GetFiles("/proc/self/fd").Length;
            using var queuedCancel = new CancellationTokenSource();
            var queuedRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = lifetime.CreateLinuxWaiter(_ => queuedRegistered.TrySetResult()).WaitAsync(queuedCancel.Token);
            Check(!queuedRegistered.Task.IsCompleted && !queued.IsCompleted, "Dedicated wait capacity was not bounded");
            queuedCancel.Cancel();
            try { await queued.WaitAsync(Limit); throw new Exception("Queued wait did not cancel"); }
            catch (OperationCanceledException) { }
            Check(!queuedRegistered.Task.IsCompleted && Directory.GetFiles("/proc/self/fd").Length <= peak + 2,
                "Queued cancellation allocated member notifications or leaked descriptors");
            cancel.Cancel();
            foreach (var wait in pending)
                try { await wait.WaitAsync(Limit); throw new Exception("Active capacity wait did not cancel"); }
                catch (OperationCanceledException) { }
            Check(!lifetime.IsEmpty && Directory.GetFiles("/proc/self/fd").Length <= before + 4,
                "Saturated wait cancellation lost live containment or leaked descriptors");
            Console.WriteLine($"PASS 64 dedicated waits bound native resources; queued cancellation performs no member registration, all descriptors reclaim (fd baseline {before}, peak {peak})");
        }
        finally
        {
            cancel.Cancel();
            foreach (var wait in pending) try { await wait.WaitAsync(Limit); } catch (OperationCanceledException) { }
        }
    }

    private static async Task ConcurrentAsync()
    {
        var cases = new List<(Endpoint Endpoint, Process Root, ProcessLifetime Lifetime, Socket Control, Task Wait)>();
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var endpoint = new Endpoint();
                var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, "closed")))!;
                var lifetime = ProcessLifetime.Attach(root);
                Socket? control = null;
                try
                {
                    Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Concurrent barrier missing");
                    root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
                    var child = await ReadChild(root);
                    control = await endpoint.Listener.AcceptAsync().WaitAsync(Limit);
                    await Read(control, new byte[4]);
                    root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
                    await root.WaitForExitAsync().WaitAsync(Limit);
                    var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var waiter = lifetime.CreateLinuxWaiter(ids => { if (ids.Contains(child)) registered.TrySetResult(); });
                    var wait = waiter.WaitAsync();
                    cases.Add((endpoint, root, lifetime, control, wait));
                    await registered.Task.WaitAsync(Limit);
                }
                catch
                {
                    if (!cases.Any(item => item.Root == root))
                    {
                        control?.Dispose(); lifetime.Terminate(); await root.WaitForExitAsync().WaitAsync(Limit);
                        await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
                        lifetime.Dispose(); root.Dispose(); endpoint.Dispose();
                    }
                    throw;
                }
            }
            // This normal managed child is outside every tested group. Its status belongs exclusively to .NET.
            var info = new ProcessStartInfo("sh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("printf 'OTHER_READY\\n'; read value; exit 41");
            using var other = Process.Start(info)!;
            Check(await other.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "OTHER_READY", "Other .NET root barrier missing");
            try
            {
                foreach (var item in cases.Take(3)) await item.Control.SendAsync(new byte[] { 1 });
                await Task.WhenAll(cases.Take(3).Select(item => item.Wait)).WaitAsync(Limit);
                Check(cases.Skip(3).All(item => !item.Wait.IsCompleted && !item.Lifetime.IsEmpty), "One group's reap completed another living group");
                other.StandardInput.Close();
                foreach (var item in cases.Skip(3)) await item.Control.SendAsync(new byte[] { 1 });
                await Task.WhenAll(cases.Select(item => item.Wait).Append(other.WaitForExitAsync())).WaitAsync(Limit);
                Check(other.ExitCode == 41 && cases.All(item => item.Root.ExitCode == 37 && item.Lifetime.IsEmpty), "Concurrent native reaping stole a .NET exit code or released a living group");
                Console.WriteLine("PASS Six concurrent pidfd groups reap only their own descendants while independent .NET roots retain exact exit codes");
            }
            finally { if (!other.HasExited) other.Kill(true); await other.WaitForExitAsync().WaitAsync(Limit); }
        }
        finally
        {
            foreach (var item in cases)
            {
                item.Control.Dispose();
                if (!item.Wait.IsCompleted) item.Lifetime.Terminate();
                await item.Root.WaitForExitAsync().WaitAsync(Limit);
                await item.Wait.WaitAsync(Limit);
                await item.Lifetime.WaitForCleanupAsync().WaitAsync(Limit);
                item.Lifetime.Dispose(); item.Root.Dispose(); item.Endpoint.Dispose();
            }
        }
    }

    private static async Task NativeRegistrationFaultsAsync()
    {
        using var endpoint = new Endpoint();
        using var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, "closed")))!;
        using var lifetime = ProcessLifetime.Attach(root);
        Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Native registration root not ready");
        root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
        var child = await ReadChild(root);
        using var control = await endpoint.Listener.AcceptAsync().WaitAsync(Limit);
        var packet = new byte[4]; await Read(control, packet);
        Check(BitConverter.ToInt32(packet) == child, "Native fault control identity mismatch");
        root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
        await root.WaitForExitAsync().WaitAsync(Limit);
        var baseline = Directory.GetFiles("/proc/self/fd").Length;
        try
        {
            var failedOpen = lifetime.CreateLinuxWaiter();
            failedOpen.DescriptorProcessId = _ => 0;
            try { await failedOpen.WaitAsync().WaitAsync(Limit); throw new Exception("Invalid native pidfd_open succeeded"); }
            catch (System.ComponentModel.Win32Exception error)
            {
                Check(error.NativeErrorCode == 22 && error.Message.Contains("register group-member"), "Wrong native registration error");
            }
            Check(!lifetime.IsEmpty && Directory.Exists($"/proc/{child}") && Directory.GetFiles("/proc/self/fd").Length <= baseline + 4,
                "Native registration failure lost ownership or leaked its cancellation pipe");
            Console.WriteLine("PASS Actual pidfd_open rejection closes owned resources and preserves the live orphan for subsequent cleanup");

            using (var cancel = new CancellationTokenSource())
            {
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var checks = 0;
                var mismatched = lifetime.CreateLinuxWaiter(ids => { if (ids.Contains(child)) registered.TrySetResult(); });
                mismatched.VerifiedStartIdentity = (_, actual) => ++checks == 1 ? "injected-replaced-start" : actual;
                var waiting = mismatched.WaitAsync(cancel.Token);
                try
                {
                    await registered.Task.WaitAsync(Limit);
                    Check(checks >= 2 && mismatched.SnapshotQueries >= 2 && !waiting.IsCompleted,
                        "Post-open identity mismatch was accepted or did not recheck stable ownership");
                }
                finally
                {
                    cancel.Cancel();
                    try { await waiting.WaitAsync(Limit); throw new Exception("Mismatched live orphan completed early"); }
                    catch (OperationCanceledException) { }
                }
                Check(!lifetime.IsEmpty && Directory.GetFiles("/proc/self/fd").Length <= baseline + 4,
                    "Rejected post-open identity retained a pidfd or abandoned its group");
            }
            Console.WriteLine("PASS Injected post-pidfd-open start mismatch closes the rejected handle and rechecks stable identity without periodic scans");

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedReap = lifetime.CreateLinuxWaiter(ids => { if (ids.Contains(child)) ready.TrySetResult(); });
            failedReap.ReapDescriptor = _ => int.MaxValue;
            var reaping = failedReap.WaitAsync();
            await ready.Task.WaitAsync(Limit);
            await control.SendAsync(new byte[] { 1 });
            try { await reaping.WaitAsync(Limit); throw new Exception("Invalid native waitid succeeded"); }
            catch (System.ComponentModel.Win32Exception error)
            {
                Check(error.NativeErrorCode == 9 && error.Message.Contains("reap stable"), "Wrong native reaping diagnostic");
            }
            Console.WriteLine($"Native reap failure resource sample groupEmpty={lifetime.IsEmpty}, childExists={Directory.Exists($"/proc/{child}")}, baselineFDs={baseline}, finalFDs={Directory.GetFiles("/proc/self/fd").Length}");
            Check(!lifetime.IsEmpty && Directory.GetFiles("/proc/self/fd").Length <= baseline + 4,
                "Failed waitid reported cleanup or leaked owned descriptors");
            var recovered = lifetime.CreateLinuxWaiter();
            await recovered.WaitAsync().WaitAsync(Limit);
            Check(recovered.Reaped == 1 && lifetime.IsEmpty, "Failed native reaping did not retain its recoverable adopted child");
            Console.WriteLine("PASS Actual waitid invalid-descriptor failure retains the adopted zombie; a fresh authoritative waiter reaps it once");
        }
        finally
        {
            lifetime.Terminate();
            await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
        }
    }

    private static async Task MemberBudgetAsync()
    {
        using var endpoint = new Endpoint();
        using var root = Process.Start(ProcessLifetime.Prepare(Info("root", endpoint.Path, "two")))!;
        using var lifetime = ProcessLifetime.Attach(root);
        var controls = new List<Socket>();
        try
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Budget root barrier missing");
            root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
            var children = new[] { await ReadChild(root), await ReadChild(root) };
            foreach (var child in children)
            {
                var socket = await endpoint.Listener.AcceptAsync().WaitAsync(Limit); controls.Add(socket);
                var packet = new byte[4]; await Read(socket, packet);
                Check(children.Contains(BitConverter.ToInt32(packet)), "Budget child identity mismatch");
            }
            root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
            await root.WaitForExitAsync().WaitAsync(Limit);
            var before = Directory.GetFiles("/proc/self/fd").Length;
            var limited = new LinuxGroupExitWaiter(root.Id, () => root.HasExited, memberLimit: 1);
            try { await limited.WaitAsync().WaitAsync(Limit); throw new Exception("Live member budget was ignored"); }
            catch (IOException error) { Check(error.Message.Contains("member limit"), "Budget failure diagnostic missing"); }
            Check(!lifetime.IsEmpty && Directory.GetFiles("/proc/self/fd").Length <= before + 4,
                "Budget failure released containment or leaked descriptors");
            // Register stable exit notifications before releasing either child. This does not reap them.
            var exits = children.Select(pid => LinuxProcessExit.WaitAsync(pid, LinuxProcessExit.StartIdentity(pid), default)).ToArray();
            foreach (var socket in controls) await socket.SendAsync(new byte[] { 1 });
            await Task.WhenAll(exits).WaitAsync(Limit);
            var cleanup = new LinuxGroupExitWaiter(root.Id, () => root.HasExited, memberLimit: 1);
            await cleanup.WaitAsync().WaitAsync(Limit);
            Check(cleanup.Reaped == 2 && lifetime.IsEmpty && root.ExitCode == 37,
                "An oversized exited tree could not be reaped within its live-watch budget");
            Console.WriteLine("PASS Live member quota retains containment on failure; streaming adopted-zombie reaping cleans an oversized exited tree without exceeding its watch budget");
        }
        finally
        {
            foreach (var socket in controls) socket.Dispose();
            if (!root.HasExited) lifetime.Terminate();
            await root.WaitForExitAsync().WaitAsync(Limit);
            await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
        }
    }

    private static async Task PipelineAsync()
    {
        using var endpoint = new Endpoint();
        using var repositories = new RepositoryService();
        using var cancellation = new CancellationTokenSource();
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "linux-group-pipeline", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        Check((await RepositoryService.RunAsync(RepositoryService.GitStart(path, ["init"]), null, default)).ExitCode == 0, "Pipeline Git init failed");
        var repository = await repositories.OpenAsync(path);
        repositories.PrepareProcess = (start, _) =>
        {
            var fixture = Info("root", endpoint.Path, "pipeline");
            start.FileName = fixture.FileName; start.ArgumentList.Clear();
            foreach (var argument in fixture.ArgumentList) start.ArgumentList.Add(argument);
            return Task.CompletedTask;
        };
        var store = new OperationStore();
        var operation = store.Register("owner:pipeline", "pipeline", "test", out _);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = operation.ExecuteAsync(TimeSpan.FromSeconds(30), async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            var result = await repositories.ExecuteCustomAsync(repository.Id, "fixture", operation.Id, _ => { }, linked.Token);
            executed.TrySetResult();
            await publish.Task.WaitAsync(linked.Token);
            return new(operation.Id, result.ExitCode == 0 ? "succeeded" : "failed", result);
        });
        var controls = new List<Socket>();
        Process? root = null;
        Task<FileResult>? writing = null;
        try
        {
            Socket? rootControl = null; Socket? childControl = null;
            for (var index = 0; index < 2; index++)
            {
                var socket = await endpoint.Listener.AcceptAsync().WaitAsync(Limit); controls.Add(socket);
                var packet = new byte[4]; await Read(socket, packet);
                var pid = BitConverter.ToInt32(packet);
                if (pid < 0) { rootControl = socket; root = Process.GetProcessById(-pid); }
                else childControl = socket;
            }
            Check(rootControl != null && childControl != null && root != null, "Actual pipeline root/child barriers missing");
            writing = repositories.FileAsync(new FileRequest(repository.Id, "write", System.IO.Path.Combine(path, "after-cleanup"), ContentBase64: "YQ=="), cancellation.Token);
            await rootControl!.SendAsync(new byte[] { 1 });
            await root!.WaitForExitAsync().WaitAsync(Limit);
            await Task.Delay(250);
            Check(!running.IsCompleted && !operation.Completion.IsCompleted && !writing.IsCompleted && store.HasActive,
                "Actual Linux pipe/root exit published a terminal result or released the repository lock");
            try { store.BeginShutdown(); throw new Exception("Actual Linux orphan allowed shutdown"); } catch (InvalidOperationException) { }
            Check(!store.TryBeginIdleShutdown(), "Actual Linux orphan allowed idle shutdown");
            await childControl!.SendAsync(new byte[] { 1 });
            await executed.Task.WaitAsync(Limit); await writing.WaitAsync(Limit);
            Check(!operation.Completion.IsCompleted && store.HasActive && File.ReadAllText(System.IO.Path.Combine(path, "after-cleanup")) == "a",
                "Linux execution-return/Complete gap lost lock or terminal ownership");
            try { store.Clear(); throw new Exception("Linux pre-Complete gap allowed owner takeover"); } catch (InvalidOperationException) { }
            publish.TrySetResult(); await running.WaitAsync(Limit);
            Check(operation.State == "succeeded" && operation.Info().Result!.ExitCode == 0, "Actual Linux tree cleanup failed to preserve its successful result");
            store.BeginShutdown();
            Console.WriteLine("PASS Actual Linux RunAsync retains lock/terminal/shutdown gates after root and pipe exit, then releases the lock before one Complete publication");
        }
        finally
        {
            foreach (var control in controls) control.Dispose();
            publish.TrySetResult(); cancellation.Cancel(); await running.WaitAsync(Limit);
            if (writing != null) try { await writing.WaitAsync(Limit); } catch (OperationCanceledException) { }
            root?.Dispose(); repositories.Close(repository.LeaseId);
        }
    }

    internal static async Task ChildAsync(string mode, string path, string detail)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux fixture required");
        if (mode == "root")
        {
            Console.WriteLine("ROOT_READY");
            var action = await Console.In.ReadLineAsync();
            if (detail != "pipeline" && action != "spawn") return;
            var info = detail == "zombie" ? ZombieParent(path) : Info(detail == "branch" ? "branch" : "leaf", path, "");
            info.RedirectStandardOutput = detail != "pipes"; info.RedirectStandardError = detail != "pipes";
            using var child = Process.Start(info)!;
            if (info.RedirectStandardOutput) Check(await child.StandardOutput.ReadLineAsync() == "LEAF_READY", "Child ready barrier missing");
            Console.WriteLine("CHILD:" + child.Id);
            if (detail == "two")
            {
                using var second = Process.Start(Info("leaf", path, ""))!;
                Check(await second.StandardOutput.ReadLineAsync() == "LEAF_READY", "Second child ready barrier missing");
                Console.WriteLine("CHILD:" + second.Id);
            }
            if (detail == "pipeline")
            {
                using var control = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await control.ConnectAsync(new UnixDomainSocketEndPoint(path));
                await control.SendAsync(BitConverter.GetBytes(-Environment.ProcessId));
                await control.ReceiveAsync(new byte[1]);
                Environment.ExitCode = 0;
                return;
            }
            await Console.In.ReadLineAsync();
            Environment.ExitCode = 37;
            return;
        }
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await socket.SendAsync(BitConverter.GetBytes(Environment.ProcessId));
        Console.WriteLine("LEAF_READY");
        var packet = new byte[1];
        if (await socket.ReceiveAsync(packet) == 0) return;
        if (mode == "branch")
        {
            using var grand = Process.Start(Info("leaf", path, ""))!;
            Check(await grand.StandardOutput.ReadLineAsync() == "LEAF_READY", "Grandchild ready barrier missing");
        }
    }

    private static ProcessStartInfo ZombieParent(string path)
    {
        // Native parent deliberately keeps its exited child unreaped. sigwait observes the exit
        // without polling or consuming its status; .NET's managed-child reaper is not involved.
        var info = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("""
            import os, signal, socket, struct, sys
            signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGCHLD})
            child = os.fork()
            if child == 0:
                os._exit(17)
            signal.sigwait({signal.SIGCHLD})
            control = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            control.connect(sys.argv[1])
            control.sendall(struct.pack('i', os.getpid()))
            print('LEAF_READY', flush=True)
            control.recv(1)
            os._exit(0)
            """);
        info.ArgumentList.Add(path);
        return info;
    }

    private static ProcessStartInfo Info(string mode, string path, string detail)
    {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (System.IO.Path.GetFileNameWithoutExtension(executable) == "dotnet") info.ArgumentList.Add(typeof(LinuxGroupChecks).Assembly.Location);
        foreach (var argument in new[] { "--linux-group-child", mode, "--endpoint", path, "--detail", detail }) info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task<int> ReadChild(Process root)
    {
        while (await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) is { } line)
            if (line.StartsWith("CHILD:", StringComparison.Ordinal)) return int.Parse(line[6..]);
        throw new IOException("Root closed stdout before its child identity barrier");
    }

    private static async Task Read(Socket socket, byte[] packet)
    {
        for (var offset = 0; offset < packet.Length;)
        {
            var read = await socket.ReceiveAsync(packet.AsMemory(offset)).AsTask().WaitAsync(Limit);
            if (read == 0) throw new IOException("Fixture control socket closed");
            offset += read;
        }
    }
    private sealed class Endpoint : IDisposable
    {
        internal string Path { get; } = "/tmp/sg-group-" + Guid.NewGuid().ToString("N") + ".sock";
        internal Socket Listener { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        internal Endpoint() { Listener.Bind(new UnixDomainSocketEndPoint(Path)); Listener.Listen(16); }
        public void Dispose() { Listener.Dispose(); File.Delete(Path); }
    }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetGroup(int pid);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
