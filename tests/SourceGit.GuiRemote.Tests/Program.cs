using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.Remote;
using SourceGit.RemoteClient;

if (args.Contains("--avatar-checks"))
{
    AvatarChecks.Run();
    return;
}
if (args.Contains("--launcher-retry-checks"))
{
    LauncherRetryChecks.Run();
    return;
}

if (args.Contains("--fetch-integration-checks"))
{
    FetchIntegrationChecks.Run();
    return;
}

if (args.Contains("--native-ui-time-checks"))
{
    UiTimeChecks.Run(nativeWindows: true);
    return;
}

if (args.Contains("--ui-time-checks"))
{
    UiTimeChecks.Run();
    return;
}

if (args.Contains("--watcher-checks"))
{
    WatcherChecks.Run();
    WatcherChecks.Diagnostics();
    await WatcherChecks.RealAsync();
    return;
}

if (args.Contains("--fetch-schedule-checks"))
{
    FetchScheduleChecks.Run();
    return;
}

if (args.Contains("--fetch-cancel-checks"))
{
    await FetchCancellationChecks.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--fake-tool")
{
    var report = args[1]; var mode = args[2];
    var files = args.Skip(3).ToArray();
    File.WriteAllLines(report, files.Select(file => Convert.ToBase64String(File.ReadAllBytes(file))));
    if (mode == "wait-merge")
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(report + ".release") && DateTime.UtcNow < until) await Task.Delay(50);
        Check(File.Exists(report + ".release"), "Fake merge release was not signalled");
    }
    if (mode.EndsWith("merge", StringComparison.Ordinal)) File.WriteAllText(files[3], "merged by Windows test tool\n");
    return;
}

// Exercise the adapters actually used by the GUI, without booting an Avalonia renderer.
var root = Path.GetFullPath(Path.Combine("artifacts", "gui-remote-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var recordPath = Option("--record") ?? Path.Combine(root, "server.json");
Process? server = null;
var stderr = Task.FromResult("");
if (Option("--record") == null)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.GetFullPath(Option("--server") ?? (args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "modules/SourceGit.Server/bin/Debug/net10.0/sourcegit-server.dll")));
    start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
    server = Process.Start(start) ?? throw new IOException("Server failed to start");
    stderr = server.StandardError.ReadToEndAsync();
    _ = server.StandardOutput.ReadToEndAsync();
}
await using var client = new RemoteRepositoryClient();
try
{
    for (var i = 0; i < 100 && !File.Exists(recordPath) && server?.HasExited != true; i++) await Task.Delay(100);
    if (!File.Exists(recordPath)) throw new IOException("No server record: " + await stderr);
    var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath), RemoteJson.Default.ServerRecord)!;
    // The protocol loops must not capture the caller's synchronization context: a UI-thread caller that
    // sync-waits on an RPC (RemoteWorkspace.List/Stat use GetAwaiter().GetResult()) would deadlock, because the
    // continuation delivering the response would be posted to the blocked UI dispatcher.
    // Runs before the harness's own client connects: a second live handshake would be rejected as an owner conflict.
    {
        var ctx = new UiQueueContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        var uiClient = new RemoteRepositoryClient();
        try
        {
            var connect = uiClient.ConnectAsync("127.0.0.1", record.Port, record.Token);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!connect.IsCompleted)
            {
                Check(DateTime.UtcNow < deadline, "UI-context connect did not complete while pumping");
                ctx.Pump(20); Thread.Sleep(10);
            }
            await connect; // already complete: no continuation is posted to the unpumped queue
            var home = uiClient.GetEnvironmentAsync().GetAwaiter().GetResult().Home;
            var entries = uiClient.ListDirectoryAsync(home).GetAwaiter().GetResult();
            Check(entries.Entries != null, "UI-context sync ListDirectory returned no directory array");
            Console.WriteLine("PASS UI-thread sync-wait on RPC does not deadlock protocol loops");
        }
        finally
        {
            // Dispose after removing the context so its continuation is not posted to the unpumped queue.
            SynchronizationContext.SetSynchronizationContext(null);
            await uiClient.DisposeAsync();
        }
    }
    await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
    var fixtureRoot = Option("--root") ?? root;
    var repositoryPath = Option("--root") is { } remoteRoot ? remoteRoot.TrimEnd('/') + "/repo space 中文" : Path.Combine(root, "repo space 中文");
    await client.FileAsync(new FileRequest("", "mkdir", repositoryPath));
    var init = await client.ExecuteGitAsync(new GitRequest("", ["init"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: repositoryPath));
    Check(init.ExitCode == 0, init.Error);
    var workspace = new RemoteWorkspace(Guid.NewGuid().ToString("N"), "GUI adapters", client, await client.GetEnvironmentAsync());
    var info = await workspace.OpenAsync(repositoryPath);
    var resource = workspace.Resource(info.WorktreePath);
    await LeaseReleaseChecks.RunAsync(record, client, repositoryPath);
    await WelcomeStatusChecks.RunAsync(resource);
    var welcomeStatus = await new SourceGit.Commands.QueryRepositoryStatus(resource).GetResultAsync();
    Check(welcomeStatus.InstanceId == client.InstanceId && welcomeStatus.RepositoryId == info.Id,
        "Welcome status command omitted its temporary repository session identity");
    using (var cancelledStatus = new CancellationTokenSource())
    {
        cancelledStatus.Cancel();
        Check(await new SourceGit.Commands.QueryRepositoryStatus(resource) { CancellationToken = cancelledStatus.Token }.GetResultAsync() == null,
            "Cancelled Welcome command returned a status");
    }
    {
        await using var sibling = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await sibling.ConnectAsync("127.0.0.1", record.Port, record.Token);
        var lease = await workspace.OpenTransientAsync(resource);
        await Task.WhenAll(workspace.RebindAsync(sibling), workspace.ReleaseTransientAsync(lease, client));
        Check(workspace.Connected && workspace.Client == sibling, "Rebind completed before repository synchronization");
        await workspace.RebindAsync(client);
        Console.WriteLine("PASS Rebinding and releasing a transient repository lease preserve session ownership");
    }
    {
        var tracker = new RepositorySnapshotTracker();
        Check(tracker.TryAccept(new("old-instance", "old-repository", 100), "old-instance", "old-repository"), "Initial snapshot rejected");
        Check(!tracker.TryAccept(new("old-instance", "old-repository", 99), "old-instance", "old-repository"), "Stale snapshot accepted");
        Check(tracker.TryAccept(new("new-instance", "new-repository", 0), "new-instance", "new-repository"), "Server restart kept the old generation threshold");
        Check(!tracker.TryAccept(new("old-instance", "old-repository", 101), "new-instance", "new-repository"), "Late result from old server accepted");
        Check(tracker.TryAccept(new("new-instance", "reopened-repository", 0), "new-instance", "reopened-repository"), "Reopened repository kept its expired session's generation");
        Check(!tracker.TryAccept(null!, "new-instance", "reopened-repository"), "Failed query accepted as an empty snapshot");
        Console.WriteLine("PASS Snapshot acceptance handles server restart, reopened sessions and late replies");
    }
    {
        var directory = Option("--record") == null ? Path.Combine(repositoryPath, "paged-directory") : repositoryPath.TrimEnd('/') + "/paged-directory";
        if (Option("--record") == null)
        {
            Directory.CreateDirectory(directory);
            for (var i = 0; i < 400; i++) File.WriteAllText(Path.Combine(directory, $"entry-{i:D3}.txt"), "");
        }
        else
        {
            var fixture = await client.ExecuteCustomAsync(new(info.Id, "mkdir -p paged-directory; i=0; while [ $i -lt 400 ]; do : > paged-directory/entry-$i; i=$((i+1)); done", Guid.NewGuid().ToString("N")));
            Check(fixture.ExitCode == 0, fixture.Error);
        }
        Check((await RemoteWorkspace.ListAsync(workspace.Resource(directory))).Length == 400, "Async directory listing lost an exact-size page");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await RemoteWorkspace.ListAsync(workspace.Resource(directory), cancellation.Token); throw new Exception("Cancelled listing completed"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS Async remote directory listing loads multiple pages and supports cancellation");
    }
    // The client has no configured Git executable. Only the separate server may run Git.
    Check(string.IsNullOrEmpty(SourceGit.Native.OS.GitExecutable), "Unexpected local Git configuration");
    var path = RepositoryPath.Combine(resource, "hello space 中文.txt");
    RepositoryFile.WriteAllText(path, "first\n");
    Check(RepositoryFile.ReadAllText(path) == "first\n", "GUI file adapter corrupted text");
    Console.WriteLine("PASS GUI remote text read/write without local Git configuration");
    var changes = await new SourceGit.Commands.QueryLocalChanges(resource).GetResultAsync();
    Check(changes.Any(x => x.Path == "hello space 中文.txt"), "GUI status lost file or rejected global Git options");
    Check(await new SourceGit.Commands.CountLocalChanges(resource, true).GetResultAsync() > 0, "GUI count query failed");
    Console.WriteLine("PASS GUI status/count parsers and structured global Git options");
    var gitDirectory = new SourceGit.Commands.QueryGitDir(resource).GetResult();
    Check(RepositoryPath.IsRemote(gitDirectory), "Git directory escaped the remote resource boundary");
    Check(RemoteWorkspace.GitVersion(resource).Major >= 2, "GUI used local Git version instead of server version");
    Console.WriteLine("PASS GUI remote Git metadata and version capability");
    // R32: Native open actions must not pass remote URIs to the Windows shell.
    var notifications = new System.Collections.Generic.List<SourceGit.Models.Notification>();
    void OnNotification(SourceGit.Models.Notification value) => notifications.Add(value);
    SourceGit.Models.Notification.Raised += OnNotification;
    var editorTemp = string.Empty;
    try
    {
        SourceGit.Native.OS.OpenInFileManager(path);
        SourceGit.Native.OS.OpenTerminal(resource);
        editorTemp = SourceGit.Native.OS.DownloadRemoteFileForEditor(path) ?? string.Empty;
        Check(notifications.Any(x => x.Message == "Cannot open the file manager for a remote repository"), "File manager accepted a remote URI");
        Check(notifications.Any(x => x.Message == "Cannot open a local terminal for a remote repository"), "Terminal accepted a remote URI");
        Check(!string.IsNullOrEmpty(editorTemp) && File.ReadAllText(editorTemp) == "first\n", "Editor did not stage remote content to a local temporary file");
        Console.WriteLine("PASS Native open actions reject remote URIs with explicit actions");
    }
    finally
    {
        SourceGit.Models.Notification.Raised -= OnNotification;
        if (!string.IsNullOrEmpty(editorTemp)) { try { File.Delete(editorTemp); } catch { } }
    }
    using (var stream = RepositoryFile.Open(path, FileMode.Open, FileAccess.Write)) stream.SetLength(2);
    using (var stream = RepositoryFile.OpenRead(path)) Check(stream.Length == 2, "Remote stream truncation failed");
    Console.WriteLine("PASS GUI remote stream SetLength");
    Check(await new SourceGit.Commands.Config(resource).SetAsync("user.name", "GUI Test"), "Set remote user name failed");
    Check(await new SourceGit.Commands.Config(resource).SetAsync("user.email", "gui-test@example.invalid"), "Set remote email failed");
    Check(await new SourceGit.Commands.Config(resource).SetAsync("commit.gpgsign", "false"), "Disable signing failed");
    var spec = RepositoryFile.GetTempFileName(resource);
    try
    {
        await RepositoryFile.WriteAllLinesAsync(spec, ["hello space 中文.txt"]);
        var add = new SourceGit.Commands.Add(resource, spec) { RaiseError = false };
        Check(await add.ExecAsync(), "GUI remote pathspec staging failed");
        var commit = new SourceGit.Commands.Commit(resource, "GUI commit with remote temp file", false, false, false, false) { RaiseError = false };
        Check(await commit.RunAsync(), "GUI remote commit message temporary file failed");
        Console.WriteLine("PASS GUI pathspec staging and commit message run with server temporary files");
        {
            var addRemote = await client.ExecuteGitAsync(new GitRequest(info.Id, ["remote", "add", "existence-probe", info.WorktreePath], false));
            Check(addRemote.ExitCode == 0, addRemote.Error);
            var status = await client.GetStatusAsync(info.Id);
            var branch = new SourceGit.Models.Branch { Name = status.Branch! };
            var present = new SourceGit.Commands.DoesBranchExistOnRemote(resource, new SourceGit.Models.Remote { Name = "existence-probe" }, branch);
            Check(await present.GetResultAsync() == true, "Remote branch existence query rejected ls-remote");
            var missing = new SourceGit.Commands.DoesBranchExistOnRemote(resource, new SourceGit.Models.Remote { Name = "unconfigured-remote" }, branch) { RaiseError = false };
            Check(await missing.GetResultAsync() == null, "Failed existence query was interpreted as a missing branch");
            var removeRemote = await client.ExecuteGitAsync(new GitRequest(info.Id, ["remote", "remove", "existence-probe"], false));
            Check(removeRemote.ExitCode == 0, removeRemote.Error);
            Console.WriteLine("PASS Remote branch existence distinguishes lookup failure from absence");
        }
        var indexPath = RepositoryPath.Combine(gitDirectory, "index");
        var indexBefore = ReadBytes(indexPath);
        var unchangedContent = RepositoryFile.ReadAllText(path);
        await Task.Delay(1100);
        RepositoryFile.WriteAllText(path, unchangedContent);
        var refreshed = await new SourceGit.Commands.QueryLocalChanges(resource).GetResultAsync();
        Check(!refreshed.Any(x => x.Path == "hello space 中文.txt"), "Same-content rewrite unexpectedly modified tracked content");
        Check(indexBefore.SequenceEqual(ReadBytes(indexPath)), "Read-only status refreshed the index despite --no-optional-locks");
        Console.WriteLine("PASS GUI status preserves remote index with --no-optional-locks");
        // R25: the client adapter must carry the repository state generation so the GUI can ignore stale query results (plan section 7).
        long ReadGeneration()
        {
            using var probe = GitProcess.Start(new ProcessStartInfo("sourcegit-server")
            {
                WorkingDirectory = resource,
                Arguments = "status",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            probe.WaitForExitAsync().GetAwaiter().GetResult();
            return probe.Generation;
        }
        var generationBefore = ReadGeneration();
        Check(generationBefore > 0, "Client adapter did not expose the repository state generation");
        await GoodGit("commit", "--allow-empty", "-m", "generation probe");
        var generationAfter = ReadGeneration();
        Check(generationAfter > generationBefore, "Client adapter lost the repository generation after a write; stale query results are undetectable");
        Console.WriteLine("PASS Client adapter carries repository generation for stale query detection");
        // R26: read-only branch/tag/current-branch queries must not bump the repository generation;
        // a self-bumping query makes every GUI refresh re-emit RepositoryChanged forever (refresh feedback loop).
        // The probe uses --no-optional-locks so the status read itself does not refresh the index and
        // re-trigger the worktree watcher (a plain status probe would bump the generation under itself);
        // the settle wait lets any watcher debounce from the R25 commit above land before sampling.
        long ReadQuietGeneration()
        {
            using var probe = GitProcess.Start(new ProcessStartInfo("sourcegit-server")
            {
                WorkingDirectory = resource,
                Arguments = "--no-optional-locks status",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            probe.WaitForExitAsync().GetAwaiter().GetResult();
            return probe.Generation;
        }
        Task.Delay(600).GetAwaiter().GetResult();
        var quietBefore = ReadQuietGeneration();
        await new SourceGit.Commands.QueryBranches(resource).GetResultAsync();
        await new SourceGit.Commands.QueryTags(resource).GetResultAsync();
        await new SourceGit.Commands.QueryCurrentBranch(resource).GetResultAsync();
        Check(ReadQuietGeneration() == quietBefore, "Branch/tag read queries bumped the repository generation; the refresh loop re-triggers itself");
        Console.WriteLine("PASS Branch and tag read queries stay read-only without re-triggering repository events");
        // R27: WSL remote repositories must display their \\wsl.localhost path (user-visible contract):
        // UNC header with backslashes, Linux part with forward slashes (Linux style, per user preference);
        // local paths, unknown connections and non-WSL transports have no friendly form.
        var wslOptions = new[] { new ConnectionOptions("wsltest123", "Ubuntu-24.04", "Wsl", "Ubuntu-24.04"), new ConnectionOptions("sstest456", "git.example.com", "Ssh", "git.example.com") };
        var wslUri = RepositoryPath.Remote("wsltest123", "/home/lizh/gitRepo/test");
        var sshUri = RepositoryPath.Remote("sstest456", "/home/lizh/gitRepo/test");
        if (OperatingSystem.IsWindows())
        {
            Check(RemoteConnections.WslDisplayPath(wslUri, wslOptions) == @"\\wsl.localhost\Ubuntu-24.04/home/lizh/gitRepo/test", "WSL remote resource did not render its \\wsl.localhost display path with the Linux part in forward slashes");
            Check(RemoteConnections.WslDisplayPath(RepositoryPath.Remote("wsltest123", "/home/lizh/gitRepo/repo space 中文"), wslOptions) == @"\\wsl.localhost\Ubuntu-24.04/home/lizh/gitRepo/repo space 中文", "WSL display path broke on spaces or unicode");
            Check(RemoteConnections.WslDisplayPath("//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/test", wslOptions) == @"\\wsl.localhost\Ubuntu-24.04/home/lizh/gitRepo/test", "Local //wsl.localhost UNC path must render the same \\wsl.localhost display form as its remote twin");
            Check(RemoteConnections.WslDisplayPath(@"\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\test", wslOptions) == null, "Backslash UNC input must not render directly (stored form is slash-normalized before display)");
            Check(RemoteConnections.WslDisplayPath("//wsl.localhost/", wslOptions) == null, "Bare //wsl.localhost/ without distro must not render");
            Check(RemoteConnections.WslDisplayPath("//example.com/share/repo", wslOptions) == null, "Non-wsl.localhost UNC shares must not render");
        }
        Check(RemoteConnections.WslDisplayPath(sshUri, wslOptions) == null, "SSH remote resource must not get a UNC display path");
        Check(RemoteConnections.WslDisplayPath(RepositoryPath.Remote("unknown789", "/home/lizh/gitRepo/test"), wslOptions) == null, "Unknown connection must not get a display path");
        Check(RemoteConnections.WslDisplayPath(@"C:\repos\local", wslOptions) == null, "Local paths must not get a display path");
        // R28: a remote path on a disconnected (or unknown) connection must read as absent, never throw —
        // the Welcome context menu called RepositoryDirectory.Exists synchronously and the thrown IOException
        // crashed the GUI (three user crash logs, one per right-click on a disconnected remote node).
        Check(!SourceGit.Remote.RepositoryDirectory.Exists(wslUri), "RepositoryDirectory.Exists on a disconnected remote path must return false, not throw");
        Check(!SourceGit.Remote.RepositoryFile.Exists(wslUri + "/.git/HEAD"), "RepositoryFile.Exists on a disconnected remote path must return false, not throw");
        Console.WriteLine("PASS Existence checks on disconnected remote paths return false instead of throwing");
        // R29: WSL UNC repositories hit git's "dubious ownership" safety check (foreign SIDs over the 9P share)
        // and die with `fatal: detected dubious ownership` — blank tabs in the GUI. The local git launchers must
        // carry `-c safe.directory=*` for those paths only; ordinary local paths keep the check.
        if (OperatingSystem.IsWindows())
        {
            Check(RepositoryPath.IsWslUnc(@"\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\ddbot-benchmark"), "Backslash WSL UNC path must be recognized");
            Check(RepositoryPath.IsWslUnc("//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/ddbot-benchmark"), "Slash WSL UNC path must be recognized");
            Check(RepositoryPath.SafeOwnershipArg(@"\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\x") == "-c safe.directory=* ", "WSL UNC path must gain the safe.directory override");
            Check(RepositoryPath.SafeOwnershipArg(@"C:\repos\local") == string.Empty, "Ordinary local path must keep git's ownership check");
            Check(RepositoryPath.SafeOwnershipArg(RepositoryPath.Remote("wsltest123", "/home/lizh/gitRepo/x")) == string.Empty, "Remote URIs run on the server under the correct owner; no override");
            Console.WriteLine("PASS WSL UNC git launches carry safe.directory; other paths are untouched");
        }
        // R30: history dedup — a connection to the same endpoint must not be saved twice (identity = transport + target + user),
        // and the same WSL repository reached by two routes (local //wsl.localhost UNC vs a remote URI on a WSL connection)
        // must resolve to one history node, whatever route it was first added through.
        Check(RemoteConnections.SameIdentity(wslOptions[0], "wsl", "UBUNTU-24.04", null), "Same WSL endpoint with different case must dedup");
        Check(RemoteConnections.SameIdentity(wslOptions[0], "Wsl", "Ubuntu-24.04", ""), "Null and empty user are the same identity");
        Check(!RemoteConnections.SameIdentity(wslOptions[0], "Wsl", "Ubuntu-24.04", "other"), "Different user must not dedup");
        Check(!RemoteConnections.SameIdentity(wslOptions[1], "Wsl", "Ubuntu-24.04", null), "Different transport must not dedup");
        Check(!RemoteConnections.SameIdentity(wslOptions[0], "Wsl", "Ubuntu-22.04", null), "Different target must not dedup");
        if (OperatingSystem.IsWindows())
        {
            Check(RemoteConnections.WslTwinId(wslUri, wslOptions) == "//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/test", "Remote WSL URI must map to its UNC twin");
            Check(RemoteConnections.WslTwinId("//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/test", wslOptions) == wslUri, "UNC path must map back to its remote URI twin");
            Check(RemoteConnections.WslTwinId("//wsl.localhost/ubuntu-24.04/home/lizh/gitRepo/test", wslOptions) == wslUri, "Distro case must not break the UNC twin lookup");
            Check(RemoteConnections.WslTwinId(sshUri, wslOptions) == null, "SSH remote resource has no UNC twin");
            Check(RemoteConnections.WslTwinId(RepositoryPath.Remote("unknown789", "/home/x"), wslOptions) == null, "Unknown connection has no twin");
            Check(RemoteConnections.WslTwinId("//wsl.localhost/Ubuntu-24.04", wslOptions) == null, "Bare distro without path has no twin");
            Check(RemoteConnections.WslTwinId(@"C:\repos\local", wslOptions) == null, "Local paths have no twin");
            Check(RemoteConnections.WslTwinId("//example.com/share/repo", wslOptions) == null, "Non-WSL UNC has no twin");
            Console.WriteLine("PASS Cross-route WSL twins (UNC vs remote URI) resolve each other for history dedup");
        }
        Console.WriteLine("PASS Same-endpoint connections dedup by transport + target + user identity");
        // R31: a repository on a path the OS refuses to watch (e.g. //wsl.localhost UNC shares reject
        // ReadDirectoryChangesW) must degrade to polling refresh instead of silently never updating.
        {
            var stub = new WatchedRepoStub();
            var deadPath = Path.Combine(root, "no-such-dir-for-watcher");
            using var dead = new SourceGit.Models.Watcher(stub, deadPath, Path.Combine(deadPath, ".git")) { PollIntervalSeconds = 1 };
            var until = DateTime.UtcNow.AddSeconds(10);
            while (stub.Branches < 2 && DateTime.UtcNow < until) await Task.Delay(100);
            Check(stub.Branches >= 2 && stub.Commits >= 2 && stub.WorkingCopies >= 2, "Dead file watchers must degrade to polling refresh");
            Console.WriteLine("PASS Unwatchable repository paths degrade to polling refresh (WSL UNC auto-refresh)");
        }
        // R32: WSL UNC history nodes resolve to their server route (remote URI) when a saved WSL
        // connection covers the distro — the UNC share cannot deliver file-watcher notifications,
        // the server route is the one with event-driven refresh.
        if (OperatingSystem.IsWindows())
        {
            var unc = "//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/test";
            Check(RemoteConnections.PreferredRoute(unc, wslOptions) == wslUri, "WSL UNC path must prefer the server route when a connection exists");
            Check(RemoteConnections.PreferredRoute("//wsl.localhost/ubuntu-24.04/home/lizh/gitRepo/test", wslOptions) == wslUri, "Distro case must not break the route preference");
            Check(RemoteConnections.PreferredRoute(unc, new[] { wslOptions[1] }) == unc, "No WSL connection for the distro leaves the UNC route in place");
            Check(RemoteConnections.PreferredRoute("//example.com/share/repo", wslOptions) == "//example.com/share/repo", "Non-WSL UNC is untouched");
            Check(RemoteConnections.PreferredRoute(@"C:\repos\local", wslOptions) == @"C:\repos\local", "Local paths are untouched");
            Check(RemoteConnections.PreferredRoute(wslUri, wslOptions) == wslUri, "Remote URIs are untouched");

            var uncNode = new SourceGit.ViewModels.RepositoryNode { Id = unc, Name = "test", IsRepository = true };
            var localNode = new SourceGit.ViewModels.RepositoryNode { Id = @"C:\repos\local", Name = "local", IsRepository = true };
            var remoteNode = new SourceGit.ViewModels.RepositoryNode { Id = wslUri, Name = "test2", IsRepository = true };
            var nestedGroup = new SourceGit.ViewModels.RepositoryNode { Id = "group", Name = "group" };
            nestedGroup.SubNodes.Add(new SourceGit.ViewModels.RepositoryNode { Id = "//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/nested", Name = "nested", IsRepository = true });
            var plan = SourceGit.ViewModels.Preferences.PlanWslRouteMigration(new List<SourceGit.ViewModels.RepositoryNode> { uncNode, localNode, remoteNode, nestedGroup }, wslOptions);
            Check(plan != null && plan.Count == 2 && plan[unc] == wslUri && plan["//wsl.localhost/Ubuntu-24.04/home/lizh/gitRepo/nested"] == RepositoryPath.Remote("wsltest123", "/home/lizh/gitRepo/nested"), "Migration plan must remap UNC nodes (including nested ones) only");
            Check(SourceGit.ViewModels.Preferences.PlanWslRouteMigration(new List<SourceGit.ViewModels.RepositoryNode> { localNode, remoteNode }, wslOptions) == null, "No UNC nodes means no migration");
            Check(SourceGit.ViewModels.Preferences.PlanWslRouteMigration(new List<SourceGit.ViewModels.RepositoryNode> { uncNode }, new[] { wslOptions[1] }) == null, "No WSL connection means no migration");
            Console.WriteLine("PASS WSL UNC history nodes resolve to the server route for event-driven refresh");
        }
        Console.WriteLine("PASS WSL remote repositories render a \\wsl.localhost display path; other transports stay unchanged");
        var zip = RepositoryFile.GetTempFileName(resource);
        try
        {
            Check(await new SourceGit.Commands.Archive(resource, "HEAD", zip) { RaiseError = false }.ExecAsync(), "GUI archive failed");
            var localZip = Path.Combine(root, "export.zip");
            RepositoryFile.Copy(zip, localZip, true);
            using var archive = System.IO.Compression.ZipFile.OpenRead(localZip);
            Check(archive.Entries.Any(x => x.FullName == "hello space 中文.txt"), "Exported archive lost file");
            Console.WriteLine("PASS GUI archive downloads server content to explicit local output");
        }
        finally { RepositoryFile.Delete(zip); }
    }
    finally { RepositoryFile.Delete(spec); }
    var toolReport = Path.Combine(root, "tool-report.txt");
    var toolAssembly = typeof(Program).Assembly.Location;
    SourceGit.Models.DiffMergeTool Tool(string mode, bool merge) => new("dotnet", $"\"{toolAssembly}\" --fake-tool \"{toolReport}\" {mode} \"$LOCAL\" \"$REMOTE\"" + (merge ? " \"$BASE\" \"$MERGED\"" : ""));
    RepositoryFile.WriteAllText(path, "working copy\n");
    Check(await RemoteExternalTools.DiffAsync(resource, new SourceGit.Models.DiffOption(new SourceGit.Models.Change { Path = "hello space 中文.txt", WorkTree = SourceGit.Models.ChangeState.Modified }, true), Tool("diff", false)), "Local external diff process failed");
    var diffReport = File.ReadAllLines(toolReport).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
    Check(diffReport.SequenceEqual(new[] { "fi", "working copy\n" }), "External diff received wrong index/worktree snapshots");
    Console.WriteLine("PASS Windows external diff receives correct Linux snapshots and quoted paths");
    async Task<GitResult> Git(string[] arguments, bool write = false)
    {
        var result = await client.ExecuteGitAsync(new GitRequest(info.Id, arguments, !write, write ? Guid.NewGuid().ToString("N") : null));
        return result;
    }
    async Task GoodGit(params string[] arguments) { var result = await Git(arguments, true); Check(result.ExitCode == 0, result.Error); }
    await GoodGit("restore", "--", "hello space 中文.txt");
    var branchResult = await Git(["branch", "--show-current"]);
    var originalBranch = Encoding.UTF8.GetString(Convert.FromBase64String(branchResult.OutputBase64)).Trim();
    await GoodGit("checkout", "-b", "tool-conflict");
    RepositoryFile.WriteAllText(path, "theirs\n");
    await GoodGit("add", "--", "hello space 中文.txt"); await GoodGit("commit", "-m", "theirs");
    await GoodGit("checkout", originalBranch);
    RepositoryFile.WriteAllText(path, "ours\n");
    await GoodGit("add", "--", "hello space 中文.txt"); await GoodGit("commit", "-m", "ours");
    Check((await Git(["merge", "--no-edit", "tool-conflict"], true)).ExitCode != 0, "Expected fixture conflict");
    File.Delete(toolReport);
    Check(await RemoteExternalTools.MergeAsync(resource, "hello space 中文.txt", Tool("merge", true)), "External merge failed");
    var mergeReport = File.ReadAllLines(toolReport).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
    Check(mergeReport.Take(3).SequenceEqual(new[] { "ours\n", "theirs\n", "fi" }), "Merge stage ordering/content is wrong");
    Check(RepositoryFile.ReadAllText(path) == "merged by Windows test tool\n", "Merge did not upload result");
    Check(string.IsNullOrEmpty((await Git(["ls-files", "-u"])).OutputBase64), "Merge result was not staged");
    Console.WriteLine("PASS Windows external merge downloads Git stages, uploads result and stages remotely");
    await GoodGit("merge", "--abort");
    Check((await Git(["merge", "--no-edit", "tool-conflict"], true)).ExitCode != 0, "Expected second fixture conflict");
    File.Delete(toolReport);
    var staleMerge = RemoteExternalTools.MergeAsync(resource, "hello space 中文.txt", Tool("wait-merge", true));
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        while (!File.Exists(toolReport)) await Task.Delay(50, timeout.Token);
    RepositoryFile.WriteAllText(path, "concurrent remote edit\n");
    File.WriteAllText(toolReport + ".release", "continue");
    try { await staleMerge; throw new Exception("Stale external merge was accepted"); }
    catch (IOException) { }
    Check(RepositoryFile.ReadAllText(path) == "concurrent remote edit\n", "Stale tool result overwrote concurrent remote edit");
    Console.WriteLine("PASS Windows external merge rejects stale result without overwriting remote edits");
    // The GUI Submodule command class must work end-to-end on remote repositories (add + fresh-clone init via file transport).
    var subSuffix = Guid.NewGuid().ToString("N")[..8];
    var subInnerPath = fixtureRoot.TrimEnd('/', '\\') + "/sub-inner-" + subSuffix + ".git";
    await client.FileAsync(new FileRequest("", "mkdir", subInnerPath));
    Check((await client.ExecuteGitAsync(new GitRequest("", ["init", "--bare", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: subInnerPath))).ExitCode == 0, "Inner bare init failed");
    var subSeedPath = fixtureRoot.TrimEnd('/', '\\') + "/sub-seed " + subSuffix;
    await client.FileAsync(new FileRequest("", "mkdir", subSeedPath));
    Check((await client.ExecuteGitAsync(new GitRequest("", ["clone", subInnerPath, subSeedPath], false, Guid.NewGuid().ToString("N"), WorkingDirectory: fixtureRoot))).ExitCode == 0, "Inner seed clone failed");
    var subSeed = await client.OpenRepositoryAsync(subSeedPath);
    await client.ExecuteGitAsync(new GitRequest(subSeed.Id, ["config", "user.name", "Remote Test"], false, Guid.NewGuid().ToString("N")));
    await client.ExecuteGitAsync(new GitRequest(subSeed.Id, ["config", "user.email", "remote-test@example.invalid"], false, Guid.NewGuid().ToString("N")));
    await client.FileAsync(new FileRequest(subSeed.Id, "write", "inner.txt", ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("inner\n")), Truncate: true));
    Check((await client.ExecuteGitAsync(new GitRequest(subSeed.Id, ["add", "-A"], false, Guid.NewGuid().ToString("N")))).ExitCode == 0, "Inner seed add failed");
    Check((await client.ExecuteGitAsync(new GitRequest(subSeed.Id, ["commit", "-m", "inner seed"], false, Guid.NewGuid().ToString("N")))).ExitCode == 0, "Inner seed commit failed");
    Check((await client.ExecuteGitAsync(new GitRequest(subSeed.Id, ["push", "origin", "main"], false, Guid.NewGuid().ToString("N")))).ExitCode == 0, "Inner seed push failed");
    await client.CallAsync("CloseRepository", new IdRequest(subSeed.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    var subOuterPath = fixtureRoot.TrimEnd('/', '\\') + "/sub-outer " + subSuffix;
    await client.FileAsync(new FileRequest("", "mkdir", subOuterPath));
    Check((await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: subOuterPath))).ExitCode == 0, "Outer init failed");
    var subOuter = await client.OpenRepositoryAsync(subOuterPath);
    await client.ExecuteGitAsync(new GitRequest(subOuter.Id, ["config", "user.name", "Remote Test"], false, Guid.NewGuid().ToString("N")));
    await client.ExecuteGitAsync(new GitRequest(subOuter.Id, ["config", "user.email", "remote-test@example.invalid"], false, Guid.NewGuid().ToString("N")));
    Check((await client.ExecuteGitAsync(new GitRequest(subOuter.Id, ["commit", "--allow-empty", "-m", "base"], false, Guid.NewGuid().ToString("N")))).ExitCode == 0, "Outer base commit failed");
    // Resource URIs must round-trip the server's canonical worktree path, or the raw channel's WorkingDirectory check rejects the session.
    var subOuterUri = workspace.Resource(subOuter.WorktreePath);
    Check(await new SourceGit.Commands.Submodule(subOuterUri).AddAsync(subInnerPath, "inner", false), "Submodule add failed on remote repository");
    Check(RepositoryFile.Exists(subOuterUri + "/.gitmodules"), "Submodule add did not register .gitmodules");
    Check(RepositoryFile.ReadAllText(subOuterUri + "/inner/inner.txt").Replace("\r\n", "\n") == "inner\n", "Submodule content missing after add");
    await client.ExecuteGitAsync(new GitRequest(subOuter.Id, ["add", "-A"], false, Guid.NewGuid().ToString("N")));
    Check((await client.ExecuteGitAsync(new GitRequest(subOuter.Id, ["commit", "-m", "add submodule"], false, Guid.NewGuid().ToString("N")))).ExitCode == 0, "Submodule commit failed");
    var subFreshPath = fixtureRoot.TrimEnd('/', '\\') + "/sub-fresh " + subSuffix;
    await client.FileAsync(new FileRequest("", "mkdir", subFreshPath));
    Check((await client.ExecuteGitAsync(new GitRequest("", ["clone", subOuterPath, subFreshPath], false, Guid.NewGuid().ToString("N"), WorkingDirectory: fixtureRoot))).ExitCode == 0, "Fresh clone failed");
    var subFresh = await client.OpenRepositoryAsync(subFreshPath);
    var subFreshUri = workspace.Resource(subFresh.WorktreePath);
    Check(await new SourceGit.Commands.Submodule(subFreshUri).UpdateAsync(new List<string>(), true, false, false), "Submodule update --init failed on fresh clone");
    Check(RepositoryFile.ReadAllText(subFreshUri + "/inner/inner.txt").Replace("\r\n", "\n") == "inner\n", "Submodule content missing after fresh-clone update");
    await client.CallAsync("CloseRepository", new IdRequest(subOuter.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    await client.CallAsync("CloseRepository", new IdRequest(subFresh.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    Console.WriteLine("PASS GUI Submodule command adds and initializes remote submodules");
    await workspace.OpenAsync(repositoryPath);
    await workspace.OpenAsync(repositoryPath);
    using (workspace.Subscribe(resource, () => { })) { }
    for (var i = 0; i < 50; i++)
    {
        try { await client.ExecuteGitAsync(new GitRequest(info.Id, ["status", "--porcelain"])); }
        catch (IOException) { Console.WriteLine("PASS GUI memoized repository lease closes after final subscription"); return; }
        await Task.Delay(100);
    }
    throw new Exception("Repository lease leaked after final subscription closed");
}
finally
{
    if (server != null)
    {
        if (!server.HasExited) server.Kill(true);
        await server.WaitForExitAsync();
        server.Dispose();
    }
}
string? Option(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
static byte[] ReadBytes(string path) { using var input = RepositoryFile.OpenRead(path); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
sealed class UiQueueContext : SynchronizationContext
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue(() => d(state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    public void Pump(int max) { for (var i = 0; i < max && _queue.TryDequeue(out var action); i++) action(); }
}
sealed class WatchedRepoStub : SourceGit.Models.IRepository
{
    public int Branches;
    public int Commits;
    public int WorkingCopies;
    public bool MayHaveSubmodules() => false;
    public void RefreshBranches() => System.Threading.Interlocked.Increment(ref Branches);
    public void RefreshWorktrees() { }
    public void RefreshTags() { }
    public void RefreshCommits() => System.Threading.Interlocked.Increment(ref Commits);
    public void RefreshSubmodules() { }
    public void RefreshWorkingCopyChanges() => System.Threading.Interlocked.Increment(ref WorkingCopies);
    public void RefreshStashes() { }
}
