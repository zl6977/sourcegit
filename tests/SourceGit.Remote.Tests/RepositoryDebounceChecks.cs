using SourceGit.Core;

internal static class RepositoryDebounceChecks
{
    internal static void Run()
    {
        var clock = new ManualTimeProvider();
        var count = 0;
        using (var debounce = new RepositoryChangeDebounce(() => count++, clock))
        {
            clock.Advance(TimeSpan.FromHours(1));
            Check(count == 0 && clock.EnabledTimers == 0, "Idle watcher scheduled work");
            debounce.Schedule();
            var stale = clock.CaptureScheduledCallbacks().Single();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            debounce.Schedule(); stale();
            Check(count == 0, "Old timer fired a reset deadline early");
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Check(count == 1 && clock.EnabledTimers == 0, "Burst did not settle once");
            for (var i = 0; i < 20; i++)
            {
                debounce.Schedule(); clock.Advance(TimeSpan.FromMilliseconds(100));
            }
            Check(count == 2, "Continuous events exceeded maximum dirty window");
            debounce.Schedule(urgent: true); debounce.Schedule();
            clock.Advance(TimeSpan.Zero);
            Check(count == 3 && clock.EnabledTimers == 0, "Normal event delayed an overflow refresh");
        }
        Console.WriteLine("PASS Repository debounce parks idle work, rechecks old callbacks, bounds continuous events and preserves urgent refresh");

        clock = new ManualTimeProvider(); count = 0;
        RepositoryChangeDebounce? reentrant = null;
        reentrant = new RepositoryChangeDebounce(() =>
        {
            count++;
            if (count == 1) reentrant!.Schedule();
            else reentrant!.Dispose();
        }, clock);
        using (reentrant)
        {
            reentrant.Schedule(); clock.Advance(TimeSpan.FromMilliseconds(200));
            Check(count == 1 && clock.EnabledTimers == 1, "Events in notification lost the next batch");
            var late = clock.CaptureScheduledCallbacks().Single();
            clock.Advance(TimeSpan.FromMilliseconds(200));
            reentrant.Schedule(); late();
            Check(count == 2 && clock.EnabledTimers == 0, "Reentrant Dispose allowed a late callback");
        }
        Console.WriteLine("PASS Repository events during refresh form another batch; reentrant close and late callbacks remain stopped");

        clock = new ManualTimeProvider(); count = 0;
        using (var failed = new RepositoryChangeDebounce(() =>
        {
            if (++count == 1) throw new IOException("injected notification consumer failure");
        }, clock))
        {
            failed.Schedule(); clock.Advance(TimeSpan.FromMilliseconds(200));
            Check(count == 1 && clock.EnabledTimers == 1, "Failed notification was lost");
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Check(count == 1, "Failed notification retried too early");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Check(count == 2 && clock.EnabledTimers == 0, "Failed notification did not stop after success");
        }
        Console.WriteLine("PASS Failed repository notification retries only pending work and stops after success");
        clock = new ManualTimeProvider(); count = 0;
        var diagnostics = new List<string>();
        using (var retry = new RepositoryChangeDebounce(() =>
        {
            if (++count <= 8) throw new IOException("sensitive fixture token");
        }, clock) { Diagnostic = diagnostics.Add })
        {
            retry.Schedule(); clock.Advance(TimeSpan.FromMilliseconds(200));
            clock.Advance(TimeSpan.FromSeconds(8));
            Check(count == 9 && diagnostics.Count == 5 && clock.EnabledTimers == 0, "Core retry diagnostics flooded or lost recovery");
            Check(diagnostics.Last().Contains("refresh-recovered attempts=8") && diagnostics.Take(4).All(message => message.Contains("reason=IOException") && message.Contains("nextIn=1s")), "Core retry diagnostics omitted cause/count/deadline");
            Check(diagnostics.All(message => !message.Contains("sensitive fixture token")), "Core retry diagnostics exposed raw exception text");
        }
        Console.WriteLine("PASS Repository retry diagnostics coalesce failures and report recovery without raw sensitive text");
    }

    internal static async Task ConcurrentAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var count = 0;
        using var debounce = new RepositoryChangeDebounce(() =>
        {
            Interlocked.Increment(ref count); entered.Set();
            try { Check(release.Wait(TimeSpan.FromSeconds(10)), "Close test never released its callback"); }
            finally { finished.Set(); }
        });
        try
        {
            debounce.Schedule(urgent: true);
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "Actual timer never entered its callback");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 500; i++) debounce.Schedule();
            })));
            debounce.Dispose();
            release.Set();
            Check(finished.Wait(TimeSpan.FromSeconds(10)), "Admitted callback did not finish after Dispose");
            debounce.Schedule();
            Check(count == 1, "Concurrent events overlapped admitted notification");
        }
        finally { release.Set(); }
        Console.WriteLine("PASS Actual timer serializes concurrent events and lets an admitted callback finish during close");
    }

    internal static async Task RepositoryAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "repository-watcher", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var init = await RepositoryService.RunAsync(RepositoryService.GitStart(root, ["init", "--bare"]), null, default);
        Check(init.ExitCode == 0, "Bare fixture initialization failed");
        foreach (var failAt in new[] { 1, 2 })
        {
            using var service = new RepositoryService(maxLeases: 1);
            var watchers = new List<FileSystemWatcher>();
            var disposed = 0;
            service.BeforeWatcherEnabled = watcher =>
            {
                watchers.Add(watcher);
                watcher.Disposed += (_, _) => Interlocked.Increment(ref disposed);
                if (watchers.Count == failAt) throw new IOException("injected watcher initialization failure");
            };
            try
            {
                await service.OpenAsync(root);
                throw new Exception("Watcher initialization failure was swallowed");
            }
            catch (IOException error) when (error.Message == "injected watcher initialization failure") { }
            Check(watchers.Count == failAt && disposed == failAt, "Partially initialized repository retained watchers");
            Check(watchers.All(watcher => !watcher.EnableRaisingEvents), "Failed watcher remained enabled");
            service.BeforeWatcherEnabled = null;
            var opened = await service.OpenAsync(root);
            service.Close(opened.LeaseId);
            Console.WriteLine($"PASS Repository initialization fault at watcher {failAt} disposes all owned sources and permits a fresh lease");
        }

        using (var service = new RepositoryService())
        {
            var opened = await service.OpenAsync(root);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += changed =>
            {
                if (changed.RepositoryId != opened.Id) return;
                try
                {
                    Check(service.Close(opened.LeaseId) != null, "Real callback did not release its lease");
                    closed.TrySetResult();
                }
                catch (Exception error) { closed.TrySetException(error); }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "real-notification.txt"), "changed");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { service.Get(opened.Id); throw new Exception("Callback close retained the old session"); }
            catch (KeyNotFoundException) { }
            var replacement = await service.OpenAsync(root);
            Check(replacement.Id != opened.Id, "Reopen reused a closed session");
            service.Close(replacement.LeaseId);
        }
        Console.WriteLine("PASS Actual file notifications allow reentrant repository Close and a fresh replacement session without deadlock");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
