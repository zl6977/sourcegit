using SourceGit.Models;
using SourceGit.ViewModels;

internal static class FetchScheduleChecks
{
    internal static void Run()
    {
        var clock = new ManualTimeProvider();
        var deliveries = new List<long>();
        using var schedule = new FetchSchedule(deliveries.Add, clock);
        schedule.Configure(false, TimeSpan.FromMinutes(1));
        schedule.ConditionsChanged(true, true);
        clock.Advance(TimeSpan.FromHours(1));
        Check(deliveries.Count == 0 && clock.EnabledTimers == 0, "Disabled scheduler woke");
        schedule.Configure(true, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(59));
        Check(deliveries.Count == 0, "Fetch ran early");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(deliveries.Count == 1 && schedule.TryStart(deliveries[^1]), "Deadline did not admit one fetch");
        Check(!schedule.TryStart(deliveries[^1]) && clock.EnabledTimers == 0, "Fetch overlapped");
        clock.Advance(TimeSpan.FromHours(1));
        Check(deliveries.Count == 1, "Running fetch was polled");
        schedule.Completed();
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(deliveries.Count == 2, "Completion did not schedule next cycle");
        Console.WriteLine("PASS Disabled/running fetch has no timer; deadline starts once and completion starts the next cycle");

        var stale = deliveries[^1];
        schedule.ConditionsChanged(false, true);
        Check(!schedule.TryStart(stale), "Queued callback survived blocking conditions");
        clock.Advance(TimeSpan.FromHours(1));
        Check(deliveries.Count == 2 && clock.EnabledTimers == 0, "Blocked fetch was polled");
        schedule.ConditionsChanged(true, true);
        clock.Advance(TimeSpan.Zero);
        Check(deliveries.Count == 3 && schedule.TryStart(deliveries[^1]), "Overdue fetch did not resume");
        schedule.Completed();
        schedule.ConditionsChanged(true, false);
        clock.Advance(TimeSpan.FromHours(1));
        Check(clock.EnabledTimers == 0 && deliveries.Count == 3, "Missing remotes were polled");
        schedule.ConditionsChanged(true, true);
        clock.Advance(TimeSpan.Zero);
        Check(deliveries.Count == 4 && schedule.TryStart(deliveries[^1]), "New remote did not resume overdue fetch");
        schedule.Completed();
        Console.WriteLine("PASS Blocked/no-remote deadlines stay parked; restoration runs once without catch-up jobs");

        var oldCallback = clock.CaptureScheduledCallbacks().Single();
        clock.Advance(TimeSpan.FromSeconds(30));
        schedule.MarkFetched();
        oldCallback();
        clock.Advance(TimeSpan.FromSeconds(30));
        Check(deliveries.Count == 4, "Manual fetch did not reset deadline");
        clock.Advance(TimeSpan.FromSeconds(30));
        stale = deliveries[^1];
        schedule.Configure(false, TimeSpan.FromMinutes(2));
        Check(!schedule.TryStart(stale), "Disabled queued fetch started");
        schedule.Configure(true, TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(deliveries.Count == 5, "Re-enable did not restart interval");
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(deliveries.Count == 6 && schedule.TryStart(deliveries[^1]), "New interval not respected");
        schedule.Configure(true, TimeSpan.FromMinutes(3));
        clock.Advance(TimeSpan.FromHours(1));
        Check(deliveries.Count == 6, "Preference change caused overlap");
        schedule.Completed();
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(deliveries.Count == 7, "Completion did not use latest preference");
        Console.WriteLine("PASS Manual fetch, queued stale callback, re-enable and running preference changes preserve deadlines");

        schedule.Dispose(); oldCallback();
        schedule.Completed(); schedule.MarkFetched(); schedule.ConditionsChanged(true, true);
        clock.Advance(TimeSpan.FromDays(1));
        Check(deliveries.Count == 7 && clock.EnabledTimers == 0 && !schedule.TryStart(deliveries[^1]), "Disposed scheduler resurrected");
        schedule.Configure(true, TimeSpan.Zero);
        Console.WriteLine("PASS Close/late callbacks cannot restart work");
        using (var invalid = new FetchSchedule(deliveries.Add, clock))
        {
            invalid.ConditionsChanged(true, true);
            invalid.Configure(true, TimeSpan.FromMinutes(1));
            clock.Advance(TimeSpan.FromMinutes(1));
            var queued = deliveries[^1];
            invalid.Configure(true, TimeSpan.Zero);
            Check(!invalid.TryStart(queued) && clock.EnabledTimers == 0, "Zero interval retained a queued fetch");
            invalid.Configure(true, TimeSpan.FromMinutes(-1));
            clock.Advance(TimeSpan.FromHours(1));
            Check(clock.EnabledTimers == 0, "Negative interval scheduled work");
            invalid.Configure(true, TimeSpan.FromMinutes(1));
            clock.Advance(TimeSpan.FromMinutes(1));
            Check(invalid.TryStart(deliveries[^1]), "Valid interval did not restore fetch");
        }
        Console.WriteLine("PASS Nonpositive intervals disable work and invalidate queued callbacks; valid interval restores scheduling");

        // Exercise the actual page signals, including changes within the same Popup.
        var page = new LauncherPage(new RepositoryNode(), null!);
        var changes = 0;
        page.AutoFetchAvailabilityChanged += () => changes++;
        var first = new Popup();
        page.Popup = first;
        first.InProgress = true;
        Check(!page.CanCreatePopup() && changes == 2, "Same-popup busy transition missing");
        first.InProgress = false;
        Check(page.CanCreatePopup() && changes == 3, "Same-popup restoration missing");
        page.Popup = new Popup();
        first.InProgress = true;
        Check(changes == 4, "Replaced popup retained subscription");
        page.Popup = null!;
        Check(changes == 5 && page.CanCreatePopup(), "Popup removal did not wake conditions");
        Console.WriteLine("PASS Actual page signals cover popup replacement, busy/restored transitions and old subscription removal");

        var root = Path.GetFullPath(Path.Combine("artifacts", "fetch-events", Guid.NewGuid().ToString("N")));
        var worktree = Path.Combine(root, "worktree");
        var common = Path.Combine(root, "main", ".git");
        var gitDir = Path.Combine(common, "worktrees", "linked");
        Directory.CreateDirectory(worktree); Directory.CreateDirectory(gitDir);
        var lockPath = Path.Combine(gitDir, "index.lock");
        File.WriteAllText(lockPath, "held");
        using var actualWatcher = new Watcher(new QuietRepository(), worktree, common);
        var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actualWatcher.ConditionsChanged += () => { if (!File.Exists(lockPath)) unlocked.TrySetResult(); };
        File.Move(lockPath, lockPath + ".old");
        unlocked.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Check(!File.Exists(lockPath), "Worktree lock was not removed");
        Console.WriteLine("PASS Actual worktree filesystem notification or watcher activation wakes an index.lock rename");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class QuietRepository : IRepository
    {
        public bool MayHaveSubmodules() => false;
        public void RefreshBranches() { }
        public void RefreshWorktrees() { }
        public void RefreshTags() { }
        public void RefreshCommits() { }
        public void RefreshSubmodules() { }
        public void RefreshWorkingCopyChanges() { }
        public void RefreshStashes() { }
    }
}
