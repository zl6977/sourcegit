using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SourceGit.Models;
using SourceGit.ViewModels;

internal static class FetchIntegrationChecks
{
    internal static void Run()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        var preferencesField = typeof(Preferences).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = preferencesField.GetValue(null);
        var preferences = new Preferences { EnableAutoFetch = true, AutoFetchInterval = 1 };
        preferencesField.SetValue(null, preferences); // Isolated preferences; no user configuration is read/written.
        var git = SourceGit.Native.OS.GitExecutable;
        SourceGit.Native.OS.GitExecutable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "git.exe" : "git"))
            .First(File.Exists);
        var root = Path.GetFullPath(Path.Combine("artifacts", "fetch-integration", Guid.NewGuid().ToString("N")));
        var main = Path.Combine(root, "main"); var worktree = Path.Combine(root, "linked");
        Directory.CreateDirectory(main);
        Git(main, "init");
        Git(main, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "fixture");
        Git(main, "worktree", "add", "-b", "linked", worktree);
        Git(worktree, "remote", "add", "origin", Path.Combine(root, "missing-remote"));
        var gitDir = Git(worktree, "rev-parse", "--absolute-git-dir").Trim();
        var common = Path.Combine(main, ".git");
        var repository = new SourceGit.ViewModels.Repository(false, worktree, gitDir);
        Field("_workingCopy").SetValue(repository, new WorkingCopy(repository));
        using var watcher = new Watcher(new QuietRepository(), worktree, common);
        Field("_watcher").SetValue(repository, watcher);
        var remote = new SourceGit.Models.Remote { Name = "origin", URL = Path.Combine(root, "missing-remote") };
        SetRemotes(repository, [remote]);
        var clock = new ManualTimeProvider();
        repository.StartLocalFetch(clock);
        var page = new LauncherPage(new RepositoryNode(), null!);
        try
        {
            clock.Advance(TimeSpan.FromHours(1)); Dispatcher.UIThread.RunJobs();
            Check(repository.Logs.Count == 0 && clock.EnabledTimers == 0, "Repository without a page did not park");
            page.Data = repository;
            clock.Advance(TimeSpan.Zero); FinishFetch(repository);
            Check(repository.Logs.Count == 1 && repository.Logs[0].Content.Contains("fatal", StringComparison.OrdinalIgnoreCase),
                "Late page attachment did not execute and retain actual Git fetch failure");
            Console.WriteLine("PASS Actual Repository late page binding resumes an overdue fetch once; real Git failure completes its log and starts a new deadline");

            var popup = new Popup { InProgress = true }; page.Popup = popup;
            clock.Advance(TimeSpan.FromMinutes(1)); Dispatcher.UIThread.RunJobs();
            Check(repository.Logs.Count == 1 && clock.EnabledTimers == 0, "Busy popup was periodically checked");
            popup.InProgress = false; clock.Advance(TimeSpan.Zero); FinishFetch(repository);
            Check(repository.Logs.Count == 2, "Same popup restoration did not resume once");
            popup.InProgress = true;
            page.Popup = new Popup(); // Replaced busy popup must no longer affect conditions.
            popup.InProgress = false;
            clock.Advance(TimeSpan.FromSeconds(30)); repository.MarkFetched();
            clock.Advance(TimeSpan.FromSeconds(30)); Dispatcher.UIThread.RunJobs();
            Check(repository.Logs.Count == 2, "Actual manual fetch did not reset the deadline");
            clock.Advance(TimeSpan.FromSeconds(30)); FinishFetch(repository);
            Check(repository.Logs.Count == 3, "Manual fetch's next deadline was lost");
            Console.WriteLine("PASS Actual Repository popup replacement/busy restoration and manual MarkFetched preserve one overdue execution and reset deadlines");

            preferences.EnableAutoFetch = false;
            clock.Advance(TimeSpan.FromHours(1)); Dispatcher.UIThread.RunJobs();
            Check(clock.EnabledTimers == 0 && repository.Logs.Count == 3, "Preference disable left polling active");
            preferences.AutoFetchInterval = 2; preferences.EnableAutoFetch = true;
            clock.Advance(TimeSpan.FromMinutes(1)); Dispatcher.UIThread.RunJobs();
            Check(repository.Logs.Count == 3, "Re-enable ignored changed interval");
            clock.Advance(TimeSpan.FromMinutes(1)); FinishFetch(repository);
            Check(repository.Logs.Count == 4, "Preference notification did not schedule the new deadline");
            Pump(repository.ToggleAutoFetchOnRemoteAsync(remote));
            Check(remote.DisableAutoFetch && clock.EnabledTimers == 0, "Actual remote disable did not park Repository scheduling");
            clock.Advance(TimeSpan.FromHours(1));
            Pump(repository.ToggleAutoFetchOnRemoteAsync(remote));
            clock.Advance(TimeSpan.Zero); FinishFetch(repository);
            Check(!remote.DisableAutoFetch && repository.Logs.Count == 5, "Actual remote re-enable did not resume overdue fetch");
            SetRemotes(repository, []); clock.Advance(TimeSpan.FromHours(1));
            Check(clock.EnabledTimers == 0, "Empty replacement remote list was polled");
            SetRemotes(repository, [remote]); clock.Advance(TimeSpan.Zero); FinishFetch(repository);
            Check(repository.Logs.Count == 6, "Replacement remote list did not resume overdue fetch");
            Console.WriteLine("PASS Actual Repository preferences, Git-config remote toggle and replacement remote lists park/resume without periodic condition checks");

            page.Popup = new Popup { InProgress = true };
            var lockPath = Path.Combine(gitDir, "index.lock"); File.WriteAllText(lockPath, "held");
            page.Popup.InProgress = false; // Immediate authoritative check sees the worktree lock.
            clock.Advance(TimeSpan.FromMinutes(2)); Dispatcher.UIThread.RunJobs();
            Check(clock.EnabledTimers == 0 && repository.Logs.Count == 6, "Real worktree index.lock did not park Repository fetch");
            var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.ConditionsChanged += () => { if (!File.Exists(lockPath)) unlocked.TrySetResult(); };
            File.Move(lockPath, lockPath + ".old"); Pump(unlocked.Task);
            Dispatcher.UIThread.RunJobs(); clock.Advance(TimeSpan.Zero); FinishFetch(repository);
            Check(repository.Logs.Count == 7, "Actual watcher-to-Repository worktree lock rename did not resume fetch");
            Console.WriteLine("PASS Actual filesystem watcher-to-Repository event wakes a real linked-worktree index.lock rename and executes the parked fetch");

            var changedDuringRun = false;
            repository.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(repository.IsAutoFetching) && repository.IsAutoFetching && !changedDuringRun)
                { changedDuringRun = true; preferences.AutoFetchInterval = 3; }
            };
            clock.Advance(TimeSpan.FromMinutes(2)); FinishFetch(repository);
            clock.Advance(TimeSpan.FromMinutes(2)); Dispatcher.UIThread.RunJobs();
            Check(repository.Logs.Count == 8, "Running preference change caused overlap or used an old completion interval");
            clock.Advance(TimeSpan.FromMinutes(1)); FinishFetch(repository);
            Check(repository.Logs.Count == 9 && changedDuringRun, "Fetch failure did not schedule the latest preference from completion");
            Console.WriteLine("PASS Actual Repository running preference change and failed command use the latest interval from cleanup without overlap");

            var closed = false;
            repository.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(repository.IsAutoFetching) && repository.IsAutoFetching && !closed)
                { closed = true; repository.Close(); }
            };
            var stale = clock.CaptureScheduledCallbacks().Single();
            clock.Advance(TimeSpan.FromMinutes(3)); Dispatcher.UIThread.RunJobs();
            Pump((Task)Field("_localFetchTask").GetValue(repository)!);
            Check(closed && !repository.IsAutoFetching && repository.Logs[^1].IsComplete && clock.EnabledTimers == 0,
                "Reentrant Repository Close did not observe fetch cleanup");
            var count = repository.Logs.Count;
            stale(); preferences.EnableAutoFetch = false; preferences.EnableAutoFetch = true;
            page.Popup = null!; page.Data = null!;
            clock.Advance(TimeSpan.FromDays(1)); Dispatcher.UIThread.RunJobs(); repository.Close(); repository.StartLocalFetch(clock);
            Check(repository.Logs.Count == count && clock.EnabledTimers == 0, "Closed Repository retained subscriptions or restarted after stale callbacks");
            Console.WriteLine("PASS Actual Repository reentrant Close cancels/observes fetch, removes signals and rejects stale callbacks, repeated close and restart");
        }
        finally
        {
            repository.Close(); Pump((Task)Field("_localFetchTask").GetValue(repository)!);
            page.Data = null!; preferencesField.SetValue(null, previous);
            SourceGit.Native.OS.GitExecutable = git;
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void FinishFetch(SourceGit.ViewModels.Repository repository)
    {
        Dispatcher.UIThread.RunJobs();
        Pump((Task)Field("_localFetchTask").GetValue(repository)!);
        Check(!repository.IsAutoFetching && repository.Logs.Count > 0 && repository.Logs[0].IsComplete, "Fetch did not fully finish");
    }
    private static FieldInfo Field(string name) => typeof(SourceGit.ViewModels.Repository).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void SetRemotes(SourceGit.ViewModels.Repository repository, List<SourceGit.Models.Remote> remotes) =>
        typeof(SourceGit.ViewModels.Repository).GetProperty(nameof(repository.Remotes))!.SetValue(repository, remotes);
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(SourceGit.Native.OS.GitExecutable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("Fixture Git exceeded deadline"); }
        Check(process.ExitCode == 0, error.GetAwaiter().GetResult()); return output.GetAwaiter().GetResult();
    }
    private static void Pump(Task task)
    {
        var limit = Stopwatch.StartNew();
        // Test-only dispatcher pumping; product completion/condition paths use events.
        while (!task.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(15))
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Actual Repository callback exceeded test deadline");
        Dispatcher.UIThread.RunJobs(); task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class QuietRepository : IRepository
    {
        public bool MayHaveSubmodules() => false;
        public void RefreshBranches() { } public void RefreshWorktrees() { } public void RefreshTags() { }
        public void RefreshCommits() { } public void RefreshSubmodules() { } public void RefreshWorkingCopyChanges() { } public void RefreshStashes() { }
    }
}
