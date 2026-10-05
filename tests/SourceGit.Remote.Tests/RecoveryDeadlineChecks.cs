using SourceGit.RemoteClient;

internal static class RecoveryDeadlineChecks
{
    internal static async Task RunAsync(string executable)
    {
        foreach (var mode in new[] { "deadline", "dispose", "success", "installed-deadline", "diagnostic-failure" }) await CaseAsync(executable, mode);
        await DisconnectBeforeRetirementAsync(executable);
    }
    private static async Task DisconnectBeforeRetirementAsync(string executable)
    {
        await using var manager = new ConnectionManager(new ConnectionOptions("recovery-retirement-" + Guid.NewGuid().ToString("N"), "Recovery retirement", "Local", "", LocalServerPath: executable));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirements = 0;
        manager.BeforeRecoveryRetired = async () =>
        {
            if (Interlocked.Increment(ref retirements) != 1) return;
            reached.TrySetResult();
            await release.Task;
        };
        var prior = await manager.ConnectAsync();
        try
        {
            await prior.DisposeAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var installed = manager.Client!;
            await installed.DisposeAsync(); // Real Disconnected runs while the old worker owns _reconnecting.
            var recovering = installed.RecoveryClient!(default);
            if (recovering.IsCompleted) throw new Exception("Disconnected installed generation was reused");
            release.TrySetResult();
            var recovered = await recovering.WaitAsync(TimeSpan.FromSeconds(10));
            if (recovered.IsDisconnected || recovered == installed || recovered.InstanceId != prior.InstanceId)
                throw new Exception("Retiring worker lost the installed client's disconnection");
            await recovered.GetEnvironmentAsync();
            await manager.ShutdownForUpgradeAsync(recovered);
            Console.WriteLine("PASS Disconnect after installation but before worker retirement starts a fresh recovery generation without lost notification");
        }
        finally { release.TrySetResult(); }
    }
    private static async Task CaseAsync(string executable, string mode)
    {
        var options = new ConnectionOptions("recovery-budget-" + Guid.NewGuid().ToString("N"), "Recovery budget fixture", "Local", "", LocalServerPath: executable);
        var manager = new ConnectionManager(options, TimeSpan.FromSeconds(2));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;
        var callbackDiagnostics = 0;
        manager.Diagnostic += entry => { if (entry.Stage == "Connected callback") Interlocked.Increment(ref callbackDiagnostics); };
        CancellationToken installToken = default;
        manager.Connected += (_, _) =>
        {
            if (Interlocked.Increment(ref connected) != 2 || mode != "installed-deadline") return;
            using var registration = installToken.Register(() => canceled.TrySetResult());
            // Installation is already authoritative; the budget expires before
            // this deliberately failing consumer returns to its recovery worker.
            canceled.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            throw new IOException("Fixture consumer failed after authoritative installation");
        };
        RemoteRepositoryClient? cleanup = null;
        try
        {
            var prior = await manager.ConnectAsync();
            var instance = prior.InstanceId;
            manager.BeforeClientInstalled = async cancellation =>
            {
                installToken = cancellation;
                using var registration = cancellation.Register(() => canceled.TrySetResult());
                reached.TrySetResult();
                await release.Task; // Proves deadline/disposal before install, without sleep races.
            };
            if (mode == "diagnostic-failure")
            {
                manager.StateChanged += state => { if (state is "Reconnecting" or "Failed") throw new IOException("Fixture state consumer failed"); };
                await prior.DisposeAsync();
                var failure = await ExpectFailure(prior.RecoveryClient!(default));
                if (manager.State != "Failed" || failure.InnerException?.Message != "Fixture state consumer failed")
                    throw new Exception("Recovery consumer failure left waiters pending");
                manager.BeforeClientInstalled = null;
                cleanup = await manager.ConnectAsync();
                await manager.ShutdownForUpgradeAsync(cleanup); cleanup = null;
                Console.WriteLine("PASS Throwing recovery state consumer publishes one failure instead of stranding shared waiters");
                return;
            }
            await prior.DisposeAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var first = prior.RecoveryClient!(CancellationToken.None);
            var second = prior.RecoveryClient!(CancellationToken.None);
            using var caller = new CancellationTokenSource();
            var canceledWaiter = prior.RecoveryClient!(caller.Token);
            caller.Cancel();
            await ExpectCanceled(canceledWaiter);
            if (first.IsCompleted || second.IsCompleted) throw new Exception("Caller cancellation ended shared recovery");
            if (mode == "deadline")
            {
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                release.TrySetResult();
                var firstError = await ExpectFailure(first);
                var secondError = await ExpectFailure(second);
                if (!ReferenceEquals(firstError, secondError) || !firstError.Message.Contains("2 seconds") || !firstError.Message.Contains("outcome remains unknown") || manager.State != "Failed" || connected != 1)
                    throw new Exception("Recovery deadline did not publish one shared failure or allowed late Connected");
                manager.BeforeClientInstalled = null;
                cleanup = await manager.ConnectAsync();
                if (cleanup.InstanceId != instance || first.Status != TaskStatus.Faulted || second.Status != TaskStatus.Faulted || !ReferenceEquals(await prior.RecoveryClient!(default), cleanup))
                    throw new Exception("Manual recovery reused a failed generation or replaced the independent server");
                Console.WriteLine("PASS Deadline wins before client installation: shared waiters receive the same bounded unknown-outcome failure; manual recovery uses a fresh signal and the same server");
            }
            else if (mode == "dispose")
            {
                var disposal = manager.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await ExpectCanceled(first); await ExpectCanceled(second);
                if (disposal.IsCompleted) throw new Exception("Dispose abandoned its blocked installation");
                release.TrySetResult();
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
                if (connected != 1 || manager.Client != null || manager.State != "Disconnected") throw new Exception("Dispose installed a late client");
                await using var replacement = new ConnectionManager(options);
                var survivor = await replacement.ConnectAsync();
                if (survivor.InstanceId != instance) throw new Exception("Disposal terminated the independently running server");
                await replacement.ShutdownForUpgradeAsync(survivor);
                Console.WriteLine("PASS Dispose cancels every pending recovery waiter and joins installation; another manager authenticates the surviving original server");
            }
            else
            {
                release.TrySetResult();
                cleanup = await first.WaitAsync(TimeSpan.FromSeconds(10));
                if (!ReferenceEquals(cleanup, await second) || cleanup.IsDisconnected || cleanup.InstanceId != instance || connected != 2 || manager.State != "Connected")
                    throw new Exception("Installed client did not authoritatively complete shared recovery");
                await cleanup.GetEnvironmentAsync();
                if (mode == "installed-deadline")
                {
                    await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var worker = (Task)typeof(ConnectionManager).GetField("_reconnectTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
                    await worker.WaitAsync(TimeSpan.FromSeconds(10));
                    if (manager.State != "Connected" || manager.Client != cleanup || cleanup.IsDisconnected) throw new Exception("Late deadline/callback error overwrote authoritative connection success");
                    if (callbackDiagnostics != 1) throw new Exception("Installed callback failure was not diagnosed once");
                    Console.WriteLine("PASS Installed success remains authoritative when its consumer fails after the recovery budget expires; the retired worker preserves Connected");
                }
                else Console.WriteLine("PASS Client installation wins before the recovery deadline; shared waiters receive one verified usable channel and canceled waiter stays canceled");
            }
            if (cleanup != null) { await manager.ShutdownForUpgradeAsync(cleanup); cleanup = null; }
        }
        finally
        {
            release.TrySetResult();
            if (cleanup is { IsDisconnected: false }) try { await cleanup.ShutdownAsync(); } catch (IOException) { }
            await manager.DisposeAsync();
        }
    }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Recovery waiter did not cancel"); }
        catch (OperationCanceledException) { }
    }
    private static async Task<IOException> ExpectFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Recovery waiter succeeded after deadline"); }
        catch (IOException error) { return error; }
    }
}
