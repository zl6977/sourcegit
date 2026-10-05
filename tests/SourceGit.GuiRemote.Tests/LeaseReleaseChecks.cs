using SourceGit.Contracts;
using SourceGit.Remote;
using SourceGit.RemoteClient;

internal static class LeaseReleaseChecks
{
    internal static async Task RunAsync(ServerRecord record, RemoteRepositoryClient origin, string repositoryPath)
    {
        var registryId = Guid.NewGuid().ToString("N");
        var environment = await origin.GetEnvironmentAsync();
        var retiredWorkspace = new RemoteWorkspace(registryId, "Retired", origin, environment);
        var replacement = new RemoteWorkspace(registryId, "Replacement", origin, environment);
        var registryPath = replacement.Resource(repositoryPath);
        retiredWorkspace.Unregister();
        Check(RemoteWorkspace.ForPath(registryPath) == replacement, "Old workspace removed its replacement");
        replacement.Unregister();
        Check(!RemoteWorkspace.IsConnected(registryPath), "Disconnected workspace retained a registry route");
        await replacement.RebindAsync(origin);
        Check(RemoteWorkspace.ForPath(registryPath) == replacement, "Reconnect did not restore registry route");
        replacement.Unregister();
        Console.WriteLine("PASS Registry removes retired workspaces, preserves replacements and restores a reconnected route");
        await using var proxy = new LeaseReplyProxy(record.Port, 2);
        await using var faulty = new RemoteRepositoryClient(origin.OwnerId, origin.RecoveryToken);
        await faulty.ConnectAsync("127.0.0.1", proxy.Port, record.Token);
        var workspace = new RemoteWorkspace(Guid.NewGuid().ToString("N"), "Release faults", faulty, await faulty.GetEnvironmentAsync());
        var resource = workspace.Resource(repositoryPath);
        var persistent = await workspace.OpenAsync(resource);
        using var subscription = workspace.Subscribe(resource, () => { });
        var temporary = await workspace.OpenTransientAsync(resource);
        var diagnostics = new List<string>();
        workspace.ConnectionError += diagnostics.Add;
        await workspace.ReleaseTransientAsync(temporary, faulty);
        Check(!faulty.IsDisconnected && workspace.Connected && workspace.PendingReleaseCount == 1,
            "Online close failure did not retain its lease");
        await workspace.RebindAsync(faulty).WaitAsync(TimeSpan.FromSeconds(5));
        Check(workspace.PendingReleaseCount == 1 && proxy.ClosedLeases.Count == 2 && diagnostics.Count == 2,
            "Failed drain lost an entry or retried it in a busy loop");
        Check((await origin.GetStatusAsync(persistent.Id)).Generation >= 0, "Deferred retry released another lease");
        proxy.FailReplies = 0;
        await workspace.RebindAsync(faulty);
        Check(workspace.PendingReleaseCount == 0 && proxy.ClosedLeases.Count == 3,
            "Same-client rebind did not retry the retained release");
        Check((await origin.GetStatusAsync(persistent.Id)).Generation >= 0, "Executed-close retry disposed the persistent session");
        Console.WriteLine("PASS Online lease close failures survive bounded drain retries; repeated acknowledged close preserves other leases");

        var closingWorkspace = new RemoteWorkspace(Guid.NewGuid().ToString("N"), "Subscription close faults", faulty, await faulty.GetEnvironmentAsync());
        var closingResource = closingWorkspace.Resource(repositoryPath);
        var failedClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closingWorkspace.ConnectionError += _ => failedClose.TrySetResult();
        using var closingSubscription = closingWorkspace.Subscribe(closingResource, () => { });
        proxy.FailReplies = 1;
        closingSubscription.Dispose();
        await failedClose.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(closingWorkspace.PendingReleaseCount == 1 && closingWorkspace.Connected,
            "Final subscription close failure lost the cached lease");
        await closingWorkspace.RebindAsync(faulty);
        Check(closingWorkspace.PendingReleaseCount == 0, "Cached lease close was not safely retried");
        Console.WriteLine("PASS Final subscription close failure retains its cached lease until a successful bounded retry");

        proxy.FailReplies = 1;
        var retired = await workspace.OpenTransientAsync(resource);
        await workspace.ReleaseTransientAsync(retired, faulty);
        Check(workspace.PendingReleaseCount == 1, "Instance-switch fixture did not retain its failed close");
        // Only the handshake identity is changed: this isolates instance filtering
        // while the real backend gives observable counts for any incorrect close.
        await using var nextProxy = new LeaseReplyProxy(record.Port, 0, "simulated-next-instance");
        await using var next = new RemoteRepositoryClient(origin.OwnerId, origin.RecoveryToken);
        await next.ConnectAsync("127.0.0.1", nextProxy.Port, record.Token);
        await workspace.RebindAsync(next);
        Check(workspace.PendingReleaseCount == 0 && nextProxy.ClosedLeases.IsEmpty,
            "Retired old-instance lease was sent to the next instance");
        await workspace.ReleaseTransientAsync(retired, faulty);
        Check(nextProxy.ClosedLeases.IsEmpty, "Late release from an old instance was sent to the new instance");
        // A real restart would have invalidated the old cached lease. The simulated
        // backend remains alive, so explicitly clean that fixture's original lease.
        await origin.CloseRepositoryAsync(persistent.LeaseId);
        subscription.Dispose();
        await workspace.RebindAsync(next); // waits for the subscription's queued release
        Console.WriteLine("PASS Instance replacement discards retired and late old-instance leases without sending them to the new instance");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
