using SourceGit.Models;

internal static class WatcherChecks
{
    internal static void Run()
    {
        var clock = new ManualTimeProvider();
        var repo = new Repository();
        using var watcher = new Watcher(repo, clock);
        clock.Advance(TimeSpan.FromHours(1));
        Check(watcher.TimerCallbacks == 0 && clock.EnabledTimers == 0, "Idle watcher woke without an event");
        for (var i = 0; i < 100; i++) watcher.NotifyRepositoryChanged();
        clock.Advance(TimeSpan.FromMilliseconds(99));
        Check(repo.Branches == 0, "Debounce ran early");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check(repo.Branches == 1 && repo.WorkingCopies == 1 && repo.Tags == 1 && repo.Commits == 1 && clock.EnabledTimers == 0, "Burst did not merge into one refresh");
        Console.WriteLine("PASS Watcher idle has no timer wakeups; remote-style burst merges into one refresh batch");

        var first = watcher.Lock();
        var last = watcher.Lock();
        watcher.NotifyRepositoryChanged();
        clock.Advance(TimeSpan.FromHours(1));
        first.Dispose(); first.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(repo.Branches == 1 && clock.EnabledTimers == 0, "Nested lock or duplicate release allowed refresh");
        last.Dispose();
        clock.Advance(TimeSpan.Zero);
        Check(repo.Branches == 2 && repo.WorkingCopies == 2, "Final unlock failed to wake dirty work");
        Console.WriteLine("PASS Watcher nested locks retain dirty work without scanning and final unlock wakes it immediately");

        for (var i = 0; i < 10; i++)
        {
            watcher.MarkDirty(Watcher.Dirty.WorkingCopy, TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }
        Check(repo.WorkingCopies == 3, "Continuous notifications starved refresh beyond maximum debounce");
        Console.WriteLine("PASS Continuous file notifications cannot postpone a dirty category beyond the two-second maximum");

        repo.OnBranch = () => watcher.MarkDirty(Watcher.Dirty.WorkingCopy, TimeSpan.Zero);
        watcher.MarkDirty(Watcher.Dirty.Branch, TimeSpan.Zero);
        clock.Advance(TimeSpan.Zero);
        Check(repo.Branches == 3 && repo.WorkingCopies == 5, "A new event during refresh was consumed by the current batch");
        Console.WriteLine("PASS A notification during refresh schedules a subsequent batch and is not overwritten by branch refresh");

        repo.OnBranch = watcher.Dispose;
        watcher.NotifyRepositoryChanged();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Check(repo.Branches == 4 && repo.WorkingCopies == 5 && clock.EnabledTimers == 0, "Reentrant disposal allowed later refresh callbacks");
        watcher.NotifyRepositoryChanged();
        clock.Advance(TimeSpan.FromHours(1));
        Check(repo.Branches == 4 && clock.EnabledTimers == 0, "Disposed watcher rescheduled work");
        Console.WriteLine("PASS Dispose during a refresh stops remaining callbacks and future scheduling");

        var lateClock = new ManualTimeProvider();
        var lateRepo = new Repository();
        using var late = new Watcher(lateRepo, lateClock);
        late.MarkDirty(Watcher.Dirty.Tags, TimeSpan.FromSeconds(1));
        var oldCallback = lateClock.CaptureScheduledCallbacks().Single();
        lateClock.Advance(TimeSpan.FromMilliseconds(900));
        late.MarkDirty(Watcher.Dirty.Tags, TimeSpan.FromSeconds(1));
        oldCallback();
        lateClock.Advance(TimeSpan.FromMilliseconds(100));
        Check(lateRepo.Tags == 0, "An old queued callback ignored a reset deadline");
        lateClock.Advance(TimeSpan.FromMilliseconds(900));
        Check(lateRepo.Tags == 1, "Deadline reset lost its refresh");
        late.Dispose(); oldCallback();
        Check(lateRepo.Tags == 1 && lateClock.EnabledTimers == 0, "Late callback rescheduled after disposal");
        Console.WriteLine("PASS A queued old timer callback rechecks the new deadline and cannot reschedule after Dispose");

        var degradedClock = new ManualTimeProvider();
        var degradedRepo = new Repository();
        using var degraded = new Watcher(degradedRepo, degradedClock);
        using var source1 = new System.IO.FileSystemWatcher();
        using var source2 = new System.IO.FileSystemWatcher();
        degraded.SetDegraded(source1); degraded.SetDegraded(source2);
        degradedClock.Advance(TimeSpan.FromSeconds(30));
        Check(degradedRepo.Branches == 1, "Failed watcher sources lost degraded refresh");
        degraded.RecoverSource(source1);
        degradedClock.Advance(TimeSpan.FromSeconds(30));
        Check(degradedRepo.Branches == 2, "Recovery of one source hid another failed watcher");
        degraded.RecoverSource(source2);
        degradedClock.Advance(TimeSpan.FromHours(1));
        Check(degradedRepo.Branches == 2 && degradedClock.EnabledTimers == 0, "Recovered event sources retained periodic fallback");
        Console.WriteLine("PASS Only failed sources enable degraded refresh; recovering all sources stops the fallback timer");

        var admissionClock = new ManualTimeProvider();
        var admissionRepo = new Repository();
        using var admission = new Watcher(admissionRepo, admissionClock);
        IDisposable? held = null;
        admissionRepo.OnBranch = () => held = admission.Lock();
        admission.NotifyRepositoryChanged();
        admissionClock.Advance(TimeSpan.FromMilliseconds(100));
        Check(admissionRepo.Branches == 1 && admissionRepo.WorkingCopies == 0 && admissionClock.EnabledTimers == 0, "Lock acquired during batch did not suspend later callbacks");
        held!.Dispose(); admissionClock.Advance(TimeSpan.Zero);
        Check(admissionRepo.Branches == 2 && admissionRepo.WorkingCopies == 1, "Suspended batch was dropped on unlock");
        Console.WriteLine("PASS A lock acquired after batch admission suspends later callbacks and retains the batch until unlock");

        var failureClock = new ManualTimeProvider();
        var failureRepo = new Repository { OnBranch = () => throw new IOException("injected callback failure") };
        using var failure = new Watcher(failureRepo, failureClock);
        failure.NotifyRepositoryChanged(); failureClock.Advance(TimeSpan.FromMilliseconds(100));
        failureClock.Advance(TimeSpan.FromMilliseconds(999));
        Check(failureRepo.Branches == 1 && failureRepo.WorkingCopies == 0, "Failure retry became a busy loop");
        failureClock.Advance(TimeSpan.FromMilliseconds(1));
        Check(failureRepo.Branches == 2 && failureRepo.WorkingCopies == 1 && failureClock.EnabledTimers == 0, "Failed batch did not retry or kept polling after success");
        Console.WriteLine("PASS Actual callback failure retains a batch with bounded retry and stops scheduling after recovery");
    }
    internal static async Task RealAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "gui-watcher", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var sources = new List<FileSystemWatcher>();
        var disposed = 0;
        using (var failed = new Watcher(new Repository(), root, Path.Combine(root, "missing-git-dir"), source =>
        {
            sources.Add(source);
            source.Disposed += (_, _) => Interlocked.Increment(ref disposed);
        }))
        {
            Check(sources.Count == 2 && disposed == 2, "Failed metadata setup retained GUI watcher sources");
            Check(sources.All(source => !source.EnableRaisingEvents), "Failed GUI source remained enabled");
        }
        Console.WriteLine("PASS GUI watcher initialization failure disposes both partially configured sources before degraded fallback");

        var git = Path.Combine(root, ".git");
        Directory.CreateDirectory(git);
        sources.Clear();
        var repo = new Repository();
        using var actual = new Watcher(repo, root, git, sources.Add);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actual.ConditionsChanged += () => ready.TrySetResult();
        if (sources.All(source => source.EnableRaisingEvents)) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.OnWorkingCopy = () =>
        {
            actual.Dispose();
            closed.TrySetResult();
        };
        await File.WriteAllTextAsync(Path.Combine(root, "actual-change.txt"), "changed");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) actual.NotifyRepositoryChanged();
        })));
        Check(repo.WorkingCopies == 1 && sources.All(source => !source.EnableRaisingEvents),
            "Actual notification reentrant close admitted more refreshes or retained sources");
        Console.WriteLine("PASS Actual GUI file notification supports reentrant close; concurrent late notifications stay stopped");
    }
    internal static void Diagnostics()
    {
        var clock = new ManualTimeProvider();
        var messages = new List<string>();
        using var source = new FileSystemWatcher();
        using var watcher = new Watcher(new Repository(), clock) { Diagnostic = messages.Add };
        for (var i = 0; i < 100; i++) watcher.SetDegraded(source);
        Check(messages.Count == 1 && messages[0].Contains("degraded-enter reason=source-failure") && messages[0].Contains("nextIn="),
            "Repeated degraded errors flooded diagnostics or omitted the next deadline");
        clock.Advance(TimeSpan.FromSeconds(240));
        Check(messages.Count(message => message.Contains("degraded-refresh")) == 4 && messages.Any(message => message.Contains("count=8")),
            "Repeated degraded refresh diagnostics were not coalesced at powers of two");
        watcher.RecoverSource(source);
        Check(messages.Last().Contains("source-recovered degraded=False") && messages.Last().Contains("fallbackCount=8"),
            "Recovery did not report state/count");
        Check(messages.All(message => message.Contains("elapsed=")) && clock.EnabledTimers == 0,
            "Diagnostic timestamps missing or recovered watcher still scheduled");
        Console.WriteLine("PASS Watcher degradation diagnostics record entry/deadline/count/recovery and coalesce repeated errors");

        clock = new ManualTimeProvider(); messages.Clear();
        var repo = new Repository();
        Action? fail = null;
        fail = () => { repo.OnBranch = fail; throw new IOException("sensitive fixture token must not appear"); };
        repo.OnBranch = fail;
        using var retries = new Watcher(repo, clock) { Diagnostic = messages.Add };
        retries.NotifyRepositoryChanged();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        clock.Advance(TimeSpan.FromSeconds(7));
        Check(messages.Count == 4 && messages.Last().Contains("attempt=8") && messages.All(message => message.Contains("reason=IOException") && message.Contains("nextIn=1s")),
            "Failure diagnostics missed type/deadline/count or were not bounded");
        Check(messages.All(message => !message.Contains("sensitive fixture token")), "Raw exception text exposed in watcher diagnostics");
        repo.OnBranch = null;
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(messages.Last().Contains("refresh-recovered attempts=8") && clock.EnabledTimers == 0,
            "Refresh recovery omitted its count or retained scheduled work");
        retries.Diagnostic = _ => throw new Exception("injected diagnostic consumer failure");
        retries.SetDegraded(source); retries.RecoverSource(source);
        Check(clock.EnabledTimers == 0, "Diagnostic consumer changed scheduler ownership");
        Console.WriteLine("PASS Refresh retry diagnostics omit raw secrets, coalesce repeated failures and cannot disrupt recovery");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Repository : IRepository
    {
        internal int Branches, WorkingCopies, Tags, Commits;
        internal Action? OnBranch;
        internal Action? OnWorkingCopy;
        public bool MayHaveSubmodules() => true;
        public void RefreshBranches() { Branches++; var callback = OnBranch; OnBranch = null; callback?.Invoke(); }
        public void RefreshWorktrees() { }
        public void RefreshTags() => Tags++;
        public void RefreshCommits() => Commits++;
        public void RefreshSubmodules() { }
        public void RefreshWorkingCopyChanges() { WorkingCopies++; var callback = OnWorkingCopy; OnWorkingCopy = null; callback?.Invoke(); }
        public void RefreshStashes() { }
    }
}
