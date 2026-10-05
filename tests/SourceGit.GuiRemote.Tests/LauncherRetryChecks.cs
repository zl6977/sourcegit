using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SourceGit.Models;
using SourceGit.ViewModels;

internal static class LauncherRetryChecks
{
    internal static void Run()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        var preferences = typeof(Preferences).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = preferences.GetValue(null);
        var isolatedPreferences = new Preferences();
        isolatedPreferences.Workspaces.Add(new Workspace());
        preferences.SetValue(null, isolatedPreferences);
        Launcher? launcher = null;
        var notifications = new List<Notification>();
        Action<Notification> capture = notifications.Add;
        Notification.Raised += capture;
        try
        {
            launcher = new Launcher(null!);
            var clock = new ManualTimeProvider();
            launcher.RemoteOpenClock = clock;
            var attempts = 0;
            CancellationToken budget = default;
            launcher.RestoreRemoteWorkspace = (_, token) =>
            {
                attempts++; budget = token;
                clock.Advance(TimeSpan.FromSeconds(89));
                throw new IOException("injected late transport failure");
            };
            var node = new RepositoryNode { Id = "sourcegit-remote://retry-fixture/repository", Name = "fixture" };
            var opening = Open(launcher, node);
            Check(attempts == 1 && !opening.IsCompleted && clock.EnabledTimers == 2, "Late failure did not enter a bounded retry wait");
            var duplicate = Open(launcher, node);
            Check(duplicate.IsCompletedSuccessfully && attempts == 1, "Same-path concurrent open bypassed admission");
            clock.Advance(TimeSpan.FromSeconds(1));
            PumpUntil(() => opening.IsCompleted);
            opening.GetAwaiter().GetResult();
            Check(budget.IsCancellationRequested && attempts == 1 && clock.EnabledTimers == 0,
                "90-second budget did not cancel the two-second failure wait or allowed another restore");
            Check(notifications.Any(notification => notification.IsError), "Budget failure was not reported to the UI");
            Console.WriteLine("PASS Actual Launcher open budget cancels a late failure backoff without another restore; duplicate open stays suppressed");

            attempts = 0; notifications.Clear();
            launcher.RestoreRemoteWorkspace = (_, _) => { attempts++; throw new InvalidOperationException("injected non-transient failure"); };
            var next = Open(launcher, node);
            PumpUntil(() => next.IsCompleted); next.GetAwaiter().GetResult();
            Check(attempts == 1 && clock.EnabledTimers == 0 && notifications.Any(notification => notification.Message == "injected non-transient failure"),
                "Finished failed open retained admission, retried a non-transient failure or lost its cause");
            Console.WriteLine("PASS Finished Launcher open releases admission; a fresh open reports a non-transient error once without a retry timer");

            attempts = 0; notifications.Clear();
            launcher.RestoreRemoteWorkspace = (_, _) => { attempts++; throw new IOException("injected repeat transport failure"); };
            var retries = Open(launcher, node);
            Check(attempts == 1 && !retries.IsCompleted, "Real transport failure did not await backoff");
            clock.Advance(TimeSpan.FromMilliseconds(1999)); Dispatcher.UIThread.RunJobs();
            Check(attempts == 1, "Retry occurred before the two-second failure deadline");
            clock.Advance(TimeSpan.FromMilliseconds(1)); PumpUntil(() => attempts == 2);
            clock.Advance(TimeSpan.FromSeconds(2)); PumpUntil(() => retries.IsCompleted);
            retries.GetAwaiter().GetResult();
            Check(attempts == 3 && clock.EnabledTimers == 0 && notifications.Any(notification => notification.Message == "injected repeat transport failure"),
                "Transport failure retried beyond three attempts or left pending timers");
            Console.WriteLine("PASS Actual Launcher transport failures retry only after deadlines, stop at three attempts and release all timers");
        }
        finally
        {
            Notification.Raised -= capture;
            if (launcher != null)
            {
                var dispatch = typeof(Launcher).GetMethod("DispatchNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Notification.Raised -= dispatch.CreateDelegate<Action<Notification>>(launcher);
            }
            preferences.SetValue(null, previous);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static Task Open(Launcher launcher, RepositoryNode node) =>
        (Task)typeof(Launcher).GetMethod("OpenRemoteRepositoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launcher, [node, null])!;
    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1); // Bounded test dispatcher pumping, not product state polling.
        }
        Check(done(), "Launcher continuation did not complete under the test dispatcher");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
