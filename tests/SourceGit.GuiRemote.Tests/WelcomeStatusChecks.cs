using SourceGit.Models;
using SourceGit.ViewModels;

internal static class WelcomeStatusChecks
{
    public static async Task RunAsync(string path)
    {
        var node = new RepositoryNode { Id = path, IsRepository = true };
        string? instance = "old-instance";
        Task Apply(RepositoryStatus? result) => node.QueryAndApplyStatusAsync(
            (_, _) => Task.FromResult(result!), _ => instance!, CancellationToken.None);
        static RepositoryStatus Status(string instance, string repository, long generation) =>
            new() { InstanceId = instance, RepositoryId = repository, Generation = generation };
        static TaskCompletionSource<RepositoryStatus> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Start(TaskCompletionSource<RepositoryStatus> reply, CancellationToken token = default) =>
            node.QueryAndApplyStatusAsync((capturedPath, capturedToken) =>
            {
                Check(capturedPath == node.Id && capturedToken == token, "Query did not capture path/cancellation");
                return reply.Task;
            }, _ => instance!, token);

        var old = Status(instance, "session-1", 50);
        await Apply(old);
        var delayedOld = Pending();
        var delayedOldTask = Start(delayedOld);
        instance = "new-instance";
        var fresh = Status(instance, "session-2", 0);
        await Apply(fresh);
        Check(node.Status == fresh, "Restart rejected generation zero");
        delayedOld.SetResult(old);
        await delayedOldTask;
        Check(node.Status == fresh, "Old instance overwrote a new result");

        await Apply(Status(instance, "session-2", 50));
        var reopened = Status(instance, "session-3", 0);
        await Apply(reopened);
        Check(node.Status == reopened, "Recreated same-instance session rejected generation zero");
        var newer = Status(instance, "session-3", 10);
        await Apply(newer);
        await Apply(Status(instance, "session-3", 9));
        Check(node.Status == newer, "Same-session stale generation accepted");

        foreach (var failure in new[] { false, true })
        {
            var delayed = Pending();
            var task = Start(delayed);
            var latest = Status(instance, "session-3", 11);
            await Apply(latest);
            delayed.SetResult(failure ? null! : Status(instance, "session-3", 12));
            await task;
            Check(node.Status == latest, "Older query success/failure overwrote the latest badge");
        }

        var retained = node.Status;
        var changedInstance = Pending();
        var changedInstanceTask = Start(changedInstance);
        instance = "third-instance";
        changedInstance.SetResult(Status("new-instance", "session-3", 100));
        await changedInstanceTask;
        Check(node.Status == retained, "Reconnect accepted an old instance without a newer query");
        await Apply(Status("new-instance", "session-3", 100));
        Check(node.Status == retained, "Reply identity differs from the captured connection");

        using (var cancellation = new CancellationTokenSource())
        {
            var delayed = Pending();
            var task = Start(delayed, cancellation.Token);
            cancellation.Cancel();
            delayed.SetResult(Status(instance, "session-4", 0));
            await task;
            Check(node.Status == retained, "Cancelled query applied its result");
        }
        var changedPath = Pending();
        var changedPathTask = Start(changedPath);
        node.Id = path + "-changed";
        node.Id = path; // Returning to the original path must not revive the old query.
        changedPath.SetResult(Status(instance, "session-4", 0));
        await changedPathTask;
        Check(node.Status == retained, "Path change revived an old query");

        var disconnected = Pending();
        var disconnectedTask = Start(disconnected);
        instance = null;
        disconnected.SetResult(null!);
        await disconnectedTask;
        Check(node.Status == retained, "Disconnected query cleared the current badge");

        node.Id = Path.GetTempPath();
        var local = new RepositoryStatus { Generation = 50 };
        await Apply(local);
        var localZero = new RepositoryStatus { Generation = 0 };
        await Apply(localZero);
        Check(node.Status == localZero, "Local status incorrectly used a generation guard");
        await Apply(null);
        Check(node.Status == null, "Current local query failure did not clear the badge");
        Console.WriteLine("PASS Welcome status guards restart, recreated sessions, reordered success/failure, cancellation, path changes and disconnect");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
