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
            launcher.RestoreRemoteWorkspace = async (_, token) =>
            {
                attempts++; budget = token;
                clock.Advance(TimeSpan.FromSeconds(89));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new IOException("unreachable");
            };
            var node = new RepositoryNode { Id = "sourcegit-remote://retry-fixture/repository", Name = "fixture" };
            var opening = Open(launcher, node);
            Check(attempts == 1 && !opening.IsCompleted && clock.EnabledTimers == 1, "Open did not wait within its single connection budget");
            var duplicate = Open(launcher, node);
            Check(duplicate.IsCompletedSuccessfully && attempts == 1, "Same-path concurrent open bypassed admission");
            clock.Advance(TimeSpan.FromSeconds(1));
            PumpUntil(() => opening.IsCompleted);
            opening.GetAwaiter().GetResult();
            Check(budget.IsCancellationRequested && attempts == 1 && clock.EnabledTimers == 0,
                "90-second budget did not cancel the connection wait or allowed another restore");
            Check(notifications.Any(notification => notification.IsError), "Budget failure was not reported to the UI");
            Console.WriteLine("PASS Actual Launcher open budget cancels one shared connection wait; duplicate open stays suppressed");

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
            PumpUntil(() => retries.IsCompleted);
            retries.GetAwaiter().GetResult();
            Check(attempts == 1 && clock.EnabledTimers == 0 && notifications.Any(notification => notification.Message == "injected repeat transport failure"),
                "Launcher added connection retries or lost the original transport failure");
            Console.WriteLine("PASS Actual Launcher reports a transport failure once without adding another retry loop");
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
