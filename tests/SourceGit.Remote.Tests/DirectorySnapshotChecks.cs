using SourceGit.Contracts;
using SourceGit.Server;
using SourceGit.RemoteClient;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class DirectorySnapshotChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string diagnostic)
    {
        try { await action(); } catch (IOException error) { Check(error.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase), "Missing diagnostic: " + error.Message); return; }
        throw new Exception("Missing rejection: " + diagnostic);
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var i = 0; i < 1200; i++) File.WriteAllText(Path.Combine(root, $"file-{i:D5}"), "fixture");
        using var snapshots = new DirectorySnapshots();
        var first = await snapshots.ReadAsync("channel", new(root), default);
        var expected = Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();
        File.WriteAllText(Path.Combine(root, "000-added-after-first-page"), "new");
        File.Delete(Path.Combine(root, "file-00999"));
        var collected = new List<FileEntry>(first.Entries!);
        var cursor = first.DirectoryCursor;
        var offset = first.NextOffset;
        var pages = 1;
        while (cursor != null)
        {
            var page = await snapshots.ReadAsync("channel", new(root, offset, Cursor: cursor), default);
            collected.AddRange(page.Entries!); offset = page.NextOffset; cursor = page.DirectoryCursor; pages++;
        }
        Check(pages == 6 && snapshots.BuildCount == 1 && collected.Select(x => x.Path).SequenceEqual(expected), "Pagination rebuilt or mixed directory snapshots");
        Check(snapshots.Count == 0 && snapshots.Bytes == 0, "Last exact-size page failed to reclaim snapshot");
        Console.WriteLine("PASS 1200 entries across six exact pages enumerate/sort once and preserve membership during mutations");

        var now = DateTime.UtcNow;
        using var timed = new DirectorySnapshots(now: () => now);
        var expiring = await timed.ReadAsync("channel", new(root), default);
        now += TimeSpan.FromSeconds(61);
        await Reject(() => timed.ReadAsync("channel", new(root, 200, Cursor: expiring.DirectoryCursor), default), "refresh");
        Check(timed.Count == 0 && timed.Bytes == 0, "Expired snapshot retained memory");
        Console.WriteLine("PASS Absolute TTL releases memory and rejects expired cursor with refresh instruction");

        var clock = new ManualTimeProvider();
        using (var scheduled = new DirectorySnapshots(timeProvider: clock))
        {
            clock.Advance(TimeSpan.FromHours(1));
            Check(scheduled.ExpiryCallbacks == 0 && clock.EnabledTimers == 0, "Empty store has periodic expiry wakeups");
            var early = await scheduled.ReadAsync("channel", new(root), default);
            clock.Advance(TimeSpan.FromSeconds(10));
            var later = await scheduled.ReadAsync("channel", new(root), default);
            scheduled.Release("channel", early.DirectoryCursor!);
            clock.Advance(TimeSpan.FromSeconds(50));
            Check(scheduled.RetainedCount == 1 && scheduled.ExpiryCallbacks == 0, "Released earliest cursor left a stale timer");
            clock.Advance(TimeSpan.FromSeconds(10));
            Check(scheduled.RetainedCount == 0 && scheduled.ExpiryCallbacks == 1 && clock.EnabledTimers == 0, "One-shot expiry did not release idle memory");
            await Reject(() => scheduled.ReadAsync("channel", new(root, 200, Cursor: later.DirectoryCursor), default), "refresh");
            await scheduled.ReadAsync("channel", new(root), default);
            scheduled.Dispose();
            clock.Advance(TimeSpan.FromHours(1));
            Check(scheduled.ExpiryCallbacks == 1 && clock.EnabledTimers == 0, "Disposed expiry timer ran/rescheduled");
        }
        Console.WriteLine("PASS One-shot monotonic TTL has no empty wakeups, reschedules after early release, expires without reads, and stops on Dispose");

        var protectedPage = await snapshots.ReadAsync("channel", new(root), default);
        snapshots.Release("other-channel", protectedPage.DirectoryCursor!);
        await Reject(() => snapshots.ReadAsync("other-channel", new(root, 200, Cursor: protectedPage.DirectoryCursor), default), "another connection");
        await Reject(() => snapshots.ReadAsync("channel", new(Path.GetTempPath(), 200, Cursor: protectedPage.DirectoryCursor), default), "refresh");
        Check(snapshots.Count == 1, "Foreign release or lookup removed owned cursor");
        snapshots.ReleaseOwner("channel");
        Check(snapshots.Count == 0 && snapshots.Bytes == 0, "Disconnect retained cursor");
        Console.WriteLine("PASS Cursors are bound to path and channel; disconnect releases all owned snapshots");

        var canceled = await snapshots.ReadAsync("channel", new(root), default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        // A pre-canceled request never builds a snapshot; a known cursor's iterator
        // releases explicitly in finally even when the canceled RPC never reaches server.
        try { await snapshots.ReadAsync("channel", new(root), cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        snapshots.Release("channel", canceled.DirectoryCursor!); snapshots.Release("channel", canceled.DirectoryCursor!);
        Check(snapshots.Count == 0 && snapshots.Bytes == 0 && snapshots.BuildCount == 3, "Cancellation/release rebuilt or retained snapshot");
        Console.WriteLine("PASS Canceled first request never builds; explicit release is idempotent and reclaims memory");

        using var bounded = new DirectorySnapshots(maxSnapshots: 1);
        var held = await bounded.ReadAsync("channel", new(root), default);
        await Reject(() => bounded.ReadAsync("channel", new(root), default), "capacity");
        bounded.Release("channel", held.DirectoryCursor!);
        using var entryLimit = new DirectorySnapshots(maxEntries: 10);
        await Reject(() => entryLimit.ReadAsync("channel", new(root), default), "snapshot limit");
        Check(entryLimit.Count == 0 && entryLimit.Bytes == 0, "Failed entry-limited build retained slot");
        using var memoryLimit = new DirectorySnapshots(maxSnapshotBytes: 500);
        await Reject(() => memoryLimit.ReadAsync("channel", new(root), default), "snapshot limit");
        Check(memoryLimit.Count == 0 && memoryLimit.Bytes == 0, "Failed memory-limited build retained slot");
        using var totalLimit = new DirectorySnapshots(maxTotalBytes: 100);
        await Reject(() => totalLimit.ReadAsync("channel", new(root), default), "memory capacity");
        Check(totalLimit.Count == 0 && totalLimit.Bytes == 0, "Failed aggregate memory admission retained slot");
        Console.WriteLine("PASS Snapshot slots, per-snapshot entries/bytes and aggregate bytes enforce bounds and reclaim failed builds");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var buildingCancellation = new CancellationTokenSource();
        IEnumerable<string> BlockedEnumeration(string path)
        {
            entered.Set(); release.Wait(TimeSpan.FromSeconds(10));
            yield return Directory.EnumerateFileSystemEntries(path).First();
        }
        using var building = new DirectorySnapshots(maxSnapshots: 1, enumerate: BlockedEnumeration);
        var pending = building.ReadAsync("channel", new(root), buildingCancellation.Token);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Builder never entered enumeration");
        buildingCancellation.Cancel(); building.ReleaseOwner("channel");
        await Reject(() => building.ReadAsync("other-channel", new(root), default), "capacity");
        release.Set();
        try { await pending; throw new Exception("Building cancellation ignored"); } catch (OperationCanceledException) { }
        Check(building.Count == 0 && building.Bytes == 0, "Canceled/disconnected builder retained memory");
        var rebuilt = await building.ReadAsync("other-channel", new(root), default);
        Check(rebuilt.Entries!.Length == 1 && building.Count == 0, "Canceled builder never relinquished capacity");
        Console.WriteLine("PASS Cancellation during blocked enumeration releases snapshot while retaining build capacity until unwound");
        await ClientAsync();
        Console.WriteLine("TOTAL 8 PASS");
    }
    private static async Task ClientAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var releases = 0; var requests = 0; var expireNext = false;
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(lifetime.Token);
            using var write = new SemaphoreSlim(1);
            try
            {
                while (await Protocol.ReadAsync(socket.GetStream(), lifetime.Token) is { } request)
                {
                    JsonElement result; string? error = null;
                    if (request.Method == "Handshake") result = Protocol.Json(new HandshakeResult("directory-instance", Protocol.Version, Protocol.Build, []), RemoteJson.Default.HandshakeResult);
                    else if (request.Method == "ListDirectory")
                    {
                        Interlocked.Increment(ref requests);
                        var page = request.Data.Deserialize(RemoteJson.Default.DirectoryRequest)!;
                        Check(page.Offset == 0 ? page.Cursor == null : page.Cursor == "cursor" && page.Offset == 200, "Client mixed or restarted cursors");
                        if (page.Offset > 0 && expireNext) { error = "Directory snapshot expired; refresh the listing"; result = Protocol.Null; }
                        else result = Protocol.Json(new FileResult(Entries: [new("fixture", "/fixture", false, 0)], DirectoryCursor: page.Offset == 0 ? "cursor" : null, NextOffset: page.Offset == 0 ? 200 : 400), RemoteJson.Default.FileResult);
                    }
                    else if (request.Method == "ReleaseDirectory") { Interlocked.Increment(ref releases); result = Protocol.Json("released", RemoteJson.Default.String); }
                    else continue;
                    await Protocol.WriteAsync(socket.GetStream(), new(request.Id, "Result", result, error), write, lifetime.Token);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        });
        await using var client = new RemoteRepositoryClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "token");
            await foreach (var _ in client.DirectoryPagesAsync("/")) break;
            Check(releases == 1, "Early iterator disposal omitted cursor release");
            using var cancellation = new CancellationTokenSource();
            try { await foreach (var _ in client.DirectoryPagesAsync("/", cancellation.Token)) cancellation.Cancel(); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Check(releases == 2, "Canceled iterator omitted cursor release");
            var count = 0;
            await foreach (var _ in client.DirectoryPagesAsync("/")) count++;
            Check(count == 2 && releases == 2, "Completed listing issued an extra page or retained cursor");
            var before = requests; expireNext = true;
            await Reject(async () => { await foreach (var _ in client.DirectoryPagesAsync("/")) { } }, "refresh");
            Check(requests == before + 2 && releases == 3, "Expired listing silently rebuilt/mixed snapshots or omitted cleanup");
            Console.WriteLine("PASS Client iterator releases on early break/cancel/expiry, preserves cursor offsets and never silently rebuilds");
        }
        finally { await client.DisposeAsync(); lifetime.Cancel(); await peer.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
