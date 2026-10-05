using SourceGit.RemoteClient;

internal static class ConnectionLifecycleChecks
{
    internal static async Task RunAsync(string executable)
    {
        await StartDisposalBarrierAsync(executable, false);
        await StartDisposalBarrierAsync(executable, true);
        var id = "lifecycle-" + Guid.NewGuid().ToString("N");
        var options = new ConnectionOptions(id, "Lifecycle", "Local", "", LocalServerPath: executable);
        var manager = new ConnectionManager(options);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;
        manager.Connected += (_, _) => Interlocked.Increment(ref connected);
        manager.BeforeClientInstalled = async cancellation =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        };
        var connect = manager.ConnectAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var queued = manager.ConnectAsync();
        if (queued.IsCompleted) throw new Exception("Queued Connect bypassed the held connect gate");
        var firstDispose = manager.DisposeAsync().AsTask();
        var secondDispose = manager.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(10));
        try { await connect; throw new Exception("In-flight connection succeeded after Dispose"); }
        catch (OperationCanceledException) { }
        try { await queued; throw new Exception("Queued connection succeeded after Dispose"); }
        catch (OperationCanceledException) { }
        if (connected != 0 || manager.Client != null || manager.State != "Disconnected")
            throw new Exception("Disposed manager published a client/Connected event");
        try { await manager.ConnectAsync(); throw new Exception("Disposed manager accepted Connect"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("PASS Dispose cancels the paused installation and queued Connect, clears client, and joins repeated disposal");

        var reentrant = new ConnectionManager(options);
        Task? reentrantDispose = null;
        var reentrantConnected = 0;
        reentrant.Connected += (_, _) => reentrantConnected++;
        reentrant.StateChanged += state =>
        {
            if (state == "Connected") reentrantDispose = reentrant.DisposeAsync().AsTask();
        };
        try { await reentrant.ConnectAsync(); throw new Exception("Reentrant Dispose allowed connection installation to finish"); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        await (reentrantDispose ?? throw new Exception("State callback was not called")).WaitAsync(TimeSpan.FromSeconds(10));
        if (reentrantConnected != 0 || reentrant.Client != null) throw new Exception("StateChanged disposal was followed by Connected");
        Console.WriteLine("PASS StateChanged can reenter Dispose without a later Connected event or deadlock");

        // Disposal owns client handles, not the independent server's lifetime.
        await using var replacement = new ConnectionManager(options);
        var client = await replacement.ConnectAsync();
        if (client.IsDisconnected) throw new Exception("Disposal killed the independently running server");
        for (var generation = 0; generation < 2; generation++)
        {
            var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            replacement.BeforeClientInstalled = async cancellation =>
            {
                installing.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            };
            var prior = client;
            await prior.DisposeAsync();
            await installing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var waiterCancellation = new CancellationTokenSource();
            var canceledWaiter = prior.RecoveryClient!(waiterCancellation.Token);
            var firstWaiter = prior.RecoveryClient!(CancellationToken.None);
            var secondWaiter = prior.RecoveryClient!(CancellationToken.None);
            waiterCancellation.Cancel();
            try { await canceledWaiter; throw new Exception("Recovery waiter ignored cancellation"); } catch (OperationCanceledException) { }
            if (firstWaiter.IsCompleted || secondWaiter.IsCompleted) throw new Exception("Old generation released a pending recovery");
            release.TrySetResult();
            client = await firstWaiter.WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(client, prior) || !ReferenceEquals(client, await secondWaiter) || client.IsDisconnected || client.InstanceId != prior.InstanceId)
                throw new Exception("Recovery returned a stale channel or canceled other waiters");
        }
        replacement.BeforeClientInstalled = null;
        Console.WriteLine("PASS Two same-instance channel generations share recovery completion and isolate canceled waiters without stale wakeups");
        var reconnects = 0;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.StateChanged += state => { if (state == "Reconnecting") Interlocked.Increment(ref reconnects); };
        client.Disconnected += _ => disconnected.TrySetResult();
        await replacement.ShutdownForUpgradeAsync(client);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (reconnects != 0 || replacement.Client != null || replacement.State != "Starting")
            throw new Exception("Intentional shutdown EOF triggered automatic deployment/reconnection");
        Console.WriteLine("PASS Intentional Shutdown EOF does not trigger a second reconnect/deployment flow");
        Console.WriteLine("PASS Connect after Dispose fails immediately; another manager reuses and shuts down the surviving server");
    }

    private static async Task StartDisposalBarrierAsync(string executable, bool spawned)
    {
        var id = "start-barrier-" + Guid.NewGuid().ToString("N");
        var options = new ConnectionOptions(id, "Start disposal barrier", "Local", "", LocalServerPath: executable);
        var manager = new ConnectionManager(options);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Diagnostics.Process? process = null;
        var startCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? launchedInstance = null;
        var connected = 0;
        manager.Connected += (_, _) => connected++;
        manager.ConfigureLocalLauncher = launcher =>
        {
            if (spawned) launcher.AfterProcessStarted = async (child, ready, cancellation) =>
            {
                using var registration = cancellation.Register(() => startCanceled.TrySetResult());
                process = child;
                _ = child.SafeHandle;
                launchedInstance = await ready.WaitAsync(cancellation);
                if (string.IsNullOrEmpty(launchedInstance)) throw new Exception("Independent startup did not publish ready");
                reached.TrySetResult();
                await release.Task; // The owner must join this in-flight handle user.
                _ = child.Id; _ = child.HasExited;
            };
            else launcher.BeforeProcessStarted = async cancellation =>
            {
                using var registration = cancellation.Register(() => startCanceled.TrySetResult());
                reached.TrySetResult(); await release.Task;
            };
        };
        var connecting = manager.ConnectAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var disposal = manager.DisposeAsync().AsTask();
            await startCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposal.IsCompleted) throw new Exception("Dispose did not join the paused startup");
            if (spawned && (process == null || process.HasExited)) throw new Exception("Dispose killed the independent startup process");
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            try { await connecting; throw new Exception("Disposed startup published a connection"); }
            catch (OperationCanceledException) { }
            if (connected != 0 || manager.Client != null || manager.State != "Disconnected") throw new Exception("Disposed startup published a client/event");
            var path = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
            if (!spawned && System.IO.File.Exists(path)) throw new Exception("Pre-start disposal spawned a server");
            if (spawned)
            {
                await using var successor = new ConnectionManager(options);
                var client = await successor.ConnectAsync();
                if (process == null || client.InstanceId != launchedInstance) throw new Exception("Independent server did not survive startup disposal");
                await successor.ShutdownForUpgradeAsync(client);
            }
            Console.WriteLine("PASS Dispose joins the " + (spawned ? "spawned process before first exit inspection" : "pre-spawn") + " barrier without disposed handle access, late Connected or competing startup");
        }
        finally
        {
            release.TrySetResult();
            await manager.DisposeAsync();
            try { await connecting; } catch (OperationCanceledException) { }
        }
    }
}
