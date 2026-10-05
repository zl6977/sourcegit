using System.Diagnostics;
using SourceGit.Core;
using SourceGit.Contracts;
using SourceGit.Server;

internal static class WindowsJobChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run native Job primitives on Windows");
        foreach (var holdingPipes in new[] { false, true })
        {
            using var release = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var releaseName));
            using var root = Start("root", releaseName, holdingPipes ? "pipes" : "closed");
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Root barrier missing");
            using var lifetime = ProcessLifetime.Attach(root);
            root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
            var child = await ReadChild(root);
            var tracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var waiter = lifetime.CreateWindowsWaiter(ids => { if (ids.Contains((uint)child)) tracked.TrySetResult(); });
            var wait = waiter.WaitAsync();
            try
            {
                await tracked.Task.WaitAsync(Limit);
                root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
                await root.WaitForExitAsync().WaitAsync(Limit);
                Check(!wait.IsCompleted, "Root exit ignored living descendant");
                var queries = waiter.SnapshotQueries;
                await Task.Delay(250);
                // One root-exit reconciliation may still be queued at sampling start.
                Check(waiter.SnapshotQueries <= queries + 1 && !wait.IsCompleted, "Living descendant was polled or released");
                release.Set();
                await wait.WaitAsync(Limit);
                Check(lifetime.IsEmpty, "Wait returned while Job remained active");
                await root.StandardOutput.ReadToEndAsync().WaitAsync(Limit);
                Console.WriteLine(holdingPipes
                    ? "PASS Windows stable Job-member waits retain an orphan holding pipes without periodic scans"
                    : "PASS Windows stable Job-member waits retain an orphan with closed pipes without periodic scans");
            }
            finally { release.Set(); lifetime.Terminate(); await root.WaitForExitAsync().WaitAsync(Limit); }
        }

        using (var spawn = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var spawnName)))
        using (var release = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var releaseName)))
        using (var root = Start("root", releaseName, "branch", spawnName))
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Branch root barrier missing");
            using var lifetime = ProcessLifetime.Attach(root);
            root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
            var child = await ReadChild(root);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var grandchild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var waiter = lifetime.CreateWindowsWaiter(ids =>
            {
                if (ids.Contains((uint)child)) ready.TrySetResult();
                if (ids.Any(id => id != (uint)root.Id && id != (uint)child)) grandchild.TrySetResult();
            });
            var wait = waiter.WaitAsync();
            try
            {
                await ready.Task.WaitAsync(Limit);
                root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
                await root.WaitForExitAsync().WaitAsync(Limit);
                spawn.Set();
                await grandchild.Task.WaitAsync(Limit);
                Check(!wait.IsCompleted, "Parent exit lost subsequently created grandchild");
                release.Set(); await wait.WaitAsync(Limit);
                Check(lifetime.IsEmpty, "Grandchild Job cleanup incomplete");
                Console.WriteLine("PASS Member-exit notification discovers a grandchild created after the original root exited");
            }
            finally { spawn.Set(); release.Set(); lifetime.Terminate(); await root.WaitForExitAsync().WaitAsync(Limit); }
        }

        using (var release = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var releaseName)))
        using (var root = Start("root", releaseName, "closed"))
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Cancellation root barrier missing");
            using var lifetime = ProcessLifetime.Attach(root);
            using var waiter = lifetime.CreateWindowsWaiter();
            var waiting = waiter.WaitAsync();
            waiter.Dispose();
            try { await waiting.WaitAsync(Limit); throw new Exception("Disposed waiter continued waiting"); } catch (OperationCanceledException) { }
            Check(!root.HasExited && !lifetime.IsEmpty, "Dispose killed or released living contained process");
            using var ownProcess = Process.GetCurrentProcess();
            var baselineHandles = HandleCount(ownProcess);
            for (var index = 0; index < 30; index++)
            {
                using var cancel = new CancellationTokenSource();
                using var iteration = lifetime.CreateWindowsWaiter();
                var pending = iteration.WaitAsync(cancel.Token);
                cancel.Cancel();
                try { await pending.WaitAsync(Limit); throw new Exception("Canceled Job wait continued"); } catch (OperationCanceledException) { }
            }
            await Task.Delay(100); // Allow native unregistration acknowledgements to settle.
            Check(HandleCount(ownProcess) <= baselineHandles + 8, "Repeated canceled Job waits leaked handles");
            root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
            await root.WaitForExitAsync().WaitAsync(Limit);
            using var afterExit = lifetime.CreateWindowsWaiter();
            await afterExit.WaitAsync().WaitAsync(Limit);
            Check(afterExit.SnapshotQueries == 1, "Already-empty Job needed repeated checks");
            Console.WriteLine("PASS Dispose/repeated cancellation reclaim wait handles without killing the Job; already-exited Job completes immediately");
        }

        var cases = new List<(Process Root, ProcessLifetime Lifetime, WindowsJobExitWaiter Waiter, Task Wait)>();
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var root = Start("root", "unused", "closed");
                Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Parallel root barrier missing");
                var lifetime = ProcessLifetime.Attach(root);
                var waiter = lifetime.CreateWindowsWaiter();
                cases.Add((root, lifetime, waiter, waiter.WaitAsync()));
            }
            foreach (var item in cases.Take(3)) { item.Root.StandardInput.WriteLine("exit"); item.Root.StandardInput.Close(); }
            await Task.WhenAll(cases.Take(3).Select(item => item.Wait)).WaitAsync(Limit);
            Check(cases.Skip(3).All(item => !item.Wait.IsCompleted && !item.Lifetime.IsEmpty), "A sibling Job completed another Job's wait");
            foreach (var item in cases.Skip(3)) { item.Root.StandardInput.WriteLine("exit"); item.Root.StandardInput.Close(); }
            await Task.WhenAll(cases.Select(item => item.Wait)).WaitAsync(Limit);
            Console.WriteLine("PASS Six concurrent Jobs keep exit waits, registrations and completion ownership isolated");
        }
        finally
        {
            foreach (var item in cases)
            {
                item.Lifetime.Terminate();
                await item.Root.WaitForExitAsync().WaitAsync(Limit);
                item.Waiter.Dispose();
                try { await item.Wait.WaitAsync(Limit); } catch (OperationCanceledException) { }
                item.Lifetime.Dispose(); item.Root.Dispose();
            }
        }

        using (var root = Start("root", "unused", "closed"))
        {
            Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Failure root barrier missing");
            using var lifetime = ProcessLifetime.Attach(root);
            using var failing = lifetime.CreateWindowsWaiter(_ => throw new IOException("injected reconcile failure"));
            try { await failing.WaitAsync().WaitAsync(Limit); throw new Exception("Reconcile failure was hidden"); }
            catch (IOException error) { Check(error.Message.Contains("injected reconcile failure"), "Wrong cleanup diagnostic"); }
            Check(!lifetime.IsEmpty && !root.HasExited, "Failed reconciliation released or killed the contained tree");
            root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
            await root.WaitForExitAsync().WaitAsync(Limit);
            await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
            Console.WriteLine("PASS Failed reconciliation disposes waits while preserving Job ownership; subsequent authoritative cleanup succeeds");
        }

        await NativeFaultsAsync();
        await PipelineAsync();
    }

    private static async Task NativeFaultsAsync()
    {
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var releaseName));
        using var root = Start("root", releaseName, "closed");
        Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Native fault root barrier missing");
        using var lifetime = ProcessLifetime.Attach(root);
        try
        {
            root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
            await ReadChild(root);
            using (var resized = lifetime.CreateWindowsWaiter())
            {
                resized.InitialSnapshotCapacity = 1;
                using var cancelResize = new CancellationTokenSource();
                var waiting = resized.WaitAsync(cancelResize.Token);
                Check(resized.MemberQueries >= 2 && !waiting.IsCompleted && !lifetime.IsEmpty,
                    "Native ERROR_MORE_DATA did not expand the complete live-member snapshot");
                cancelResize.Cancel();
                try { await waiting.WaitAsync(Limit); throw new Exception("Resized live Job completed before member exit"); }
                catch (OperationCanceledException) { }
            }
            Console.WriteLine("PASS Actual native member snapshot buffer shortage resizes and retains the full live tree");
            using (var failedQuery = lifetime.CreateWindowsWaiter())
            {
                // Accounting still uses the valid owned Job. Only the actual
                // member-query syscall receives a known invalid handle.
                failedQuery.MembersQueryHandle = _ => new IntPtr(-1);
                try { await failedQuery.WaitAsync().WaitAsync(Limit); throw new Exception("Member query failure returned an empty tree"); }
                catch (System.ComponentModel.Win32Exception error)
                {
                    Check(error.NativeErrorCode == 6 && error.Message.Contains("Cannot snapshot contained processes"), "Wrong native member query diagnostic");
                }
                Check(!root.HasExited && !lifetime.IsEmpty, "Member query failure released or killed its Job");
            }
            Console.WriteLine("PASS Actual native Job member-query rejection preserves the live owned tree and reports its error");

            using var own = Process.GetCurrentProcess();
            var baseline = HandleCount(own);
            var baselineThreads = ThreadPool.ThreadCount;
            var peak = baseline;
            for (var iteration = 0; iteration < 30; iteration++)
            {
                using var failedRegistration = lifetime.CreateWindowsWaiter();
                var handles = new List<WaitHandle>();
                failedRegistration.BeforeWaitRegistered = handle =>
                {
                    handles.Add(handle);
                    peak = Math.Max(peak, HandleCount(own));
                    // Register the first real member, then make the second
                    // SafeWaitHandle invalid at the actual registration API.
                    if (handles.Count == 2) handle.Dispose();
                };
                try { await failedRegistration.WaitAsync().WaitAsync(Limit); throw new Exception("Closed wait handle registered successfully"); }
                catch (ObjectDisposedException) { }
                Check(handles.Count == 2 && handles.All(handle => handle.SafeWaitHandle.IsClosed), "Partial registration retained a member handle");
                Check(!root.HasExited && !lifetime.IsEmpty, "Registration failure released containment");
                peak = Math.Max(peak, HandleCount(own));
            }
            Check(HandleCount(own) <= baseline + 16, "Thirty partial registration failures accumulated native handles");
            Console.WriteLine($"PASS Partial actual wait-registration failures reclaim all handles and retain ownership (30 runs; handles baseline={baseline}, peak={peak}, final={HandleCount(own)})");

            for (var iteration = 0; iteration < 20; iteration++)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var resume = new ManualResetEventSlim();
                var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancel = new CancellationTokenSource();
                using var raced = lifetime.CreateWindowsWaiter();
                var first = 0;
                var memberHandles = new List<(WaitHandle Handle, IntPtr Native)>();
                raced.BeforeWaitRegistered = _ =>
                {
                    memberHandles.Add((_, _.SafeWaitHandle.DangerousGetHandle()));
                    if (Interlocked.Increment(ref first) != 1) return;
                    peak = Math.Max(peak, HandleCount(own));
                    entered.TrySetResult();
                    Check(resume.Wait(Limit), "Registration race barrier was never released");
                };
                var waiting = Task.Run(() => raced.WaitAsync(cancel.Token));
                Task? disposing = null;
                try
                {
                    await entered.Task.WaitAsync(Limit);
                    cancel.Cancel();
                    disposing = Task.Run(() => { disposalStarted.TrySetResult(); raced.Dispose(); });
                    await disposalStarted.Task.WaitAsync(Limit);
                    Check(!disposing.IsCompleted, "Dispose did not overlap the held registration gate");
                    resume.Set();
                    try { await waiting.WaitAsync(Limit); throw new Exception("Cancellation raced registration but returned success"); }
                    catch (OperationCanceledException) { }
                    await disposing.WaitAsync(Limit);
                    Check(memberHandles.All(member => member.Handle.SafeWaitHandle.IsClosed && GetProcessId(member.Native) == 0),
                        "Canceled/disposed registration retained a live process wait handle");
                    Check(!root.HasExited && !lifetime.IsEmpty, "Canceled/disposed registration released its live Job");
                    peak = Math.Max(peak, HandleCount(own));
                }
                finally
                {
                    resume.Set(); cancel.Cancel();
                    try { await waiting.WaitAsync(Limit); } catch (OperationCanceledException) { }
                    if (disposing != null) await disposing.WaitAsync(Limit);
                }
            }
            Console.WriteLine($"Job race resource sample handles={baseline}->{HandleCount(own)}, peak={peak}, poolThreads={baselineThreads}->{ThreadPool.ThreadCount}");
            // Contended managed Monitor/fixture objects can retain runtime
            // resources until collection. Native member handles were separately
            // proved closed above; collect to detect escaping fixture/wait refs.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var collectedHandles = HandleCount(own);
            Check(collectedHandles <= baseline + 8, "Registration races retained handles after collection");
            Console.WriteLine($"PASS Cancel/Dispose concurrent with actual wait registration closes native member handles and preserves ownership (20 runs; baseline={baseline}, peak={peak}, afterCollection={collectedHandles}, poolThreads={baselineThreads}->{ThreadPool.ThreadCount})");
            release.Set(); root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
            await root.WaitForExitAsync().WaitAsync(Limit);
            await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
            Check(lifetime.IsEmpty, "Authoritative cleanup did not recover after native faults");
        }
        finally
        {
            release.Set(); lifetime.Terminate();
            await root.WaitForExitAsync().WaitAsync(Limit);
            await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
        }
    }

    private static async Task PipelineAsync()
    {
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var releaseName));
        using var exitRoot = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(out var exitName));
        using var cancellation = new CancellationTokenSource();
        using var repositories = new RepositoryService();
        var path = Path.Combine(Path.GetTempPath(), "sourcegit-job-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Check((await RepositoryService.RunAsync(RepositoryService.GitStart(path, ["init"]), null, default)).ExitCode == 0, "Pipeline Git init failed");
        var repository = await repositories.OpenAsync(path);
        var ready = new TaskCompletionSource<(Process Root, Process Child)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.PrepareProcess = (start, _) =>
        {
            var fixture = Info("root", releaseName, "pipeline", exitName);
            start.FileName = fixture.FileName;
            start.ArgumentList.Clear();
            foreach (var argument in fixture.ArgumentList) start.ArgumentList.Add(argument);
            return Task.CompletedTask;
        };
        var store = new OperationStore();
        var operation = store.Register("owner:pipeline", "pipeline", "test", out _);
        var running = operation.ExecuteAsync(Limit, async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            var result = await repositories.ExecuteCustomAsync(repository.Id, "fixture", operation.Id, line =>
            {
                if (!line.StartsWith("PIPELINE:", StringComparison.Ordinal)) return;
                var ids = line.Split(':');
                ready.TrySetResult((Process.GetProcessById(int.Parse(ids[1])), Process.GetProcessById(int.Parse(ids[2]))));
            }, linked.Token);
            executed.TrySetResult();
            await publish.Task.WaitAsync(linked.Token);
            return new(operation.Id, "succeeded", result);
        });
        (Process Root, Process Child)? captured = null;
        Task<FileResult>? writing = null;
        try
        {
            captured = await ready.Task.WaitAsync(Limit);
            writing = repositories.FileAsync(new FileRequest(repository.Id, "write", Path.Combine(path, "after-cleanup"), ContentBase64: "YQ=="), cancellation.Token);
            exitRoot.Set();
            await captured.Value.Root.WaitForExitAsync().WaitAsync(Limit);
            // Observe the real pipeline after its streams can reach EOF, while the child is held by a native event.
            await Task.Delay(250);
            Check(!captured.Value.Child.HasExited && !running.IsCompleted && !operation.Completion.IsCompleted && store.HasActive,
                "Closed-pipe descendant allowed early operation completion");
            Check(!writing.IsCompleted && !File.Exists(Path.Combine(path, "after-cleanup")), "Closed-pipe descendant released the common-directory lock");
            try { store.BeginShutdown(); throw new Exception("Closed-pipe descendant allowed shutdown"); } catch (InvalidOperationException) { }
            Check(!store.TryBeginIdleShutdown(), "Closed-pipe descendant allowed idle shutdown");
            release.Set();
            await executed.Task.WaitAsync(Limit);
            await writing.WaitAsync(Limit);
            await captured.Value.Child.WaitForExitAsync().WaitAsync(Limit);
            Check(!running.IsCompleted && !operation.Completion.IsCompleted && store.HasActive,
                "Repository return prematurely published terminal state");
            try { store.BeginShutdown(); throw new Exception("Pre-Complete gap allowed shutdown"); } catch (InvalidOperationException) { }
            try { store.Clear(); throw new Exception("Pre-Complete gap allowed owner takeover"); } catch (InvalidOperationException) { }
            Check(!store.TryBeginIdleShutdown(), "Pre-Complete gap allowed idle shutdown");
            publish.TrySetResult();
            await running.WaitAsync(Limit);
            Check(operation.State == "succeeded" && File.ReadAllText(Path.Combine(path, "after-cleanup")) == "a", "Tree cleanup failed to release operation and lock");
            store.BeginShutdown();
            Console.WriteLine("PASS Actual RunAsync retains terminal state, common-directory lock and shutdown admission after root/pipe exit until descendant exit");
            Console.WriteLine("PASS Cleanup releases the repository lock before Complete, while explicit/idle shutdown and owner takeover remain blocked until terminal publication");
        }
        finally
        {
            exitRoot.Set(); release.Set(); publish.TrySetResult(); cancellation.Cancel();
            await running.WaitAsync(Limit);
            if (writing != null) try { await writing.WaitAsync(Limit); } catch (OperationCanceledException) { }
            if (captured == null && ready.Task.IsCompletedSuccessfully) captured = ready.Task.Result;
            captured?.Root.Dispose(); captured?.Child.Dispose();
            repositories.Close(repository.LeaseId);
        }
    }

    internal static async Task ChildAsync(string mode, string releaseName, string detail, string? spawnName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named Job fixture events require Windows");
        if (mode == "root")
        {
            Console.WriteLine("ROOT_READY");
            var command = await Console.In.ReadLineAsync();
            if (detail != "pipeline" && command != "spawn") return;
            var info = Info(detail == "branch" ? "branch" : "leaf", releaseName, "", spawnName);
            // Holding pipes deliberately uses the root's stdout/stderr handles.
            info.RedirectStandardOutput = detail != "pipes";
            info.RedirectStandardError = detail != "pipes";
            using var child = Process.Start(info)!;
            if (info.RedirectStandardOutput) Check(await child.StandardOutput.ReadLineAsync() == "LEAF_READY", "Descendant barrier missing");
            Console.WriteLine("CHILD:" + child.Id);
            if (detail == "pipeline")
            {
                Console.Error.WriteLine($"PIPELINE:{Environment.ProcessId}:{child.Id}");
                using var exit = EventWaitHandle.OpenExisting(spawnName!);
                exit.WaitOne();
                return;
            }
            await Console.In.ReadLineAsync();
            return;
        }
        using var release = EventWaitHandle.OpenExisting(releaseName);
        Console.WriteLine("LEAF_READY");
        if (mode == "branch")
        {
            using var spawn = EventWaitHandle.OpenExisting(spawnName!);
            spawn.WaitOne();
            using var child = Start("leaf", releaseName, "");
            Check(await child.StandardOutput.ReadLineAsync() == "LEAF_READY", "Grandchild barrier missing");
            return;
        }
        release.WaitOne();
    }

    private static string EventName(out string name) => name = "SourceGit.Job.Test." + Guid.NewGuid().ToString("N");
    private static Process Start(string mode, string release, string detail, string? spawn = null) => Process.Start(Info(mode, release, detail, spawn))!;
    private static ProcessStartInfo Info(string mode, string release, string detail, string? spawn)
    {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet") info.ArgumentList.Add(typeof(WindowsJobChecks).Assembly.Location);
        foreach (var argument in new[] { "--job-child", mode, "--release", release, "--detail", detail }) info.ArgumentList.Add(argument);
        if (spawn != null) { info.ArgumentList.Add("--spawn"); info.ArgumentList.Add(spawn); }
        return info;
    }
    private static async Task<int> ReadChild(Process root)
    {
        while (await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) is { } line)
            if (line.StartsWith("CHILD:", StringComparison.Ordinal)) return int.Parse(line[6..]);
        throw new IOException("Root did not publish its child barrier");
    }
    private static int HandleCount(Process process) { process.Refresh(); return process.HandleCount; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
