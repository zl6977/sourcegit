using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.Core;
using SourceGit.RemoteClient;

// Dependency-free integration runner. Every repository is freshly created under --root.
// --server starts a local server DLL (root defaults to a local artifacts directory); --record uses an already running WSL/SSH server, where the root defaults to a directory under the server's home directory, since the client and server filesystems differ.
var results = new List<(string Name, string Status, string Detail)>();
if (args.Contains("--fake-ssh-safety-child"))
{
    if (args.Contains("--hold")) await Task.Delay(Timeout.Infinite);
    if (!args.Contains("--no-input")) await Console.OpenStandardInput().CopyToAsync(Stream.Null);
    Console.Write(Encoding.UTF8.GetString(Convert.FromBase64String(Option("--output")!)));
    Console.Error.Write(Encoding.UTF8.GetString(Convert.FromBase64String(Option("--error")!)));
    Environment.Exit(int.Parse(Option("--exit")!));
    return;
}
if (args.Contains("--ssh-safety-checks"))
{
    await SshSafetyChecks.RunAsync();
    return;
}
if (args.Contains("--review-followup-checks"))
{
    await ReviewFollowupChecks.RunAsync();
    return;
}
if (args.Contains("--linux-group-pressure-checks"))
{
    await LinuxGroupPressureChecks.RunAsync(Option("--native-fixture")!);
    return;
}
if (args.Contains("--repository-debounce-checks"))
{
    RepositoryDebounceChecks.Run();
    await RepositoryDebounceChecks.ConcurrentAsync();
    await RepositoryDebounceChecks.RepositoryAsync();
    return;
}
if (args.Contains("--manager-persistent-write-checks"))
{
    await ManagerPersistentWriteChecks.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--recovery-deadline-checks"))
{
    await RecoveryDeadlineChecks.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--manual-publication-checks"))
{
    await ManualPublicationChecks.RunAsync();
    return;
}
if (Option("--local-intent-owner") is { } intentOwner)
{
    await LocalStartIntentChecks.OwnerAsync(intentOwner, Option("--intent-stage")!);
    return;
}
if (args.Contains("--local-start-intent-checks"))
{
    await LocalStartIntentChecks.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE") is { } startupPipe)
{
    await LocalStartHandoffChecks.ChildAsync(Option("--record")!, startupPipe);
    return;
}
if (args.Contains("--local-start-handoff-checks"))
{
    await LocalStartHandoffChecks.RunAsync();
    return;
}
if (Option("--windows-decision-lock-child") is { } decisionLockPath)
{
    await WindowsDecisionLockChecks.ChildAsync(decisionLockPath, args.Contains("legacy"));
    return;
}
if (args.Contains("--windows-decision-lock-checks"))
{
    await WindowsDecisionLockChecks.RunAsync();
    return;
}
if (Option("--ssh-lifecycle-checks") is { } sshConfig)
{
    await SshLifecycleChecks.RunAsync(Path.GetFullPath(sshConfig), Path.GetFullPath(Option("--package")!), Option("--fixture-home")!, Option("--fixture-user")!, args.Contains("--cleanup-only"));
    return;
}
if (Option("--bootstrap-endpoint-parent") is { } endpointParentRecord)
{
    await BootstrapEndpointChecks.BootstrapChildAsync(endpointParentRecord);
    return;
}
if (args.Contains("--bootstrap-fault-child"))
{
    await BootstrapFaultChecks.ChildAsync(Option("--record")!, Option("--ready-socket")!, Option("--ready-nonce")!, Option("--fault-mode")!, Option("--hold-socket")!);
    return;
}
if (args.Contains("--bootstrap-fault-checks"))
{
    await BootstrapFaultChecks.RunAsync();
    return;
}
if (Option("--bootstrap-endpoint-child") is { } endpointRecord)
{
    await BootstrapEndpointChecks.ChildAsync(endpointRecord);
    return;
}
if (args.Contains("--bootstrap-endpoint-checks"))
{
    await BootstrapEndpointChecks.RunAsync();
    return;
}
if (args.Contains("--bootstrap-endpoint-owner-checks"))
{
    await BootstrapEndpointChecks.ForeignOwnerAsync();
    return;
}
if (Option("--linux-group-child") is { } linuxMode)
{
    await LinuxGroupChecks.ChildAsync(linuxMode, Option("--endpoint")!, Option("--detail") ?? "");
    return;
}
if (args.Contains("--linux-group-checks"))
{
    await LinuxGroupChecks.RunAsync();
    return;
}
if (Option("--job-child") is { } jobMode)
{
    await WindowsJobChecks.ChildAsync(jobMode, Option("--release")!, Option("--detail") ?? "", Option("--spawn"));
    return;
}
if (args.Contains("--windows-job-checks"))
{
    await WindowsJobChecks.RunAsync();
    return;
}
if (args.Contains("--bootstrap-ready-checks"))
{
    await BootstrapReadyChecks.RunAsync(Path.GetFullPath(Option("--bootstrap-server")!));
    return;
}
if (args.Contains("--bootstrap-primitive-checks"))
{
    await BootstrapPrimitiveChecks.RunAsync();
    return;
}
if (args.Contains("--shutdown-reply-checks"))
{
    await ServerShutdownChecks.RunAsync();
    return;
}
if (args.Contains("--server-deadline-checks"))
{
    ServerDeadlineChecks.Run();
    return;
}
if (args.Contains("--server-deadline-real-checks"))
{
    await ServerDeadlineChecks.RealAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (Environment.GetEnvironmentVariable("SOURCEGIT_TEST_READY_MODE") is { } readyMode)
{
    await LocalReadyChecks.ChildAsync(Option("--record")!, readyMode);
    return;
}
if (args.Contains("--local-ready-checks"))
{
    await LocalReadyChecks.RunAsync();
    return;
}
if (args.Contains("--probe-contract-checks"))
{
    await ProbeContractChecks.RunAsync();
    return;
}
if (args.Contains("--operation-wait-checks"))
{
    await OperationWaitChecks.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--polling-metrics"))
{
    await PollingMetrics.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--connection-lifecycle-checks"))
{
    await ConnectionLifecycleChecks.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--directory-snapshot-checks"))
{
    await DirectorySnapshotChecks.RunAsync();
    return;
}
if (args.Contains("--execution-child"))
{
    await ExecutionDeadlineChecks.ChildAsync(Option("--mode")!);
    return;
}
if (args.Contains("--execution-checks"))
{
    await ExecutionDeadlineChecks.RunAsync();
    return;
}
if (args.Contains("--execution-server-checks"))
{
    await ExecutionDeadlineChecks.ServerAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (Environment.GetEnvironmentVariable("SOURCEGIT_TEST_STALL_STARTUP") == "1")
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
if (args.Contains("--local-discover-child"))
{
    await LocalStartupChecks.ChildAsync(Path.GetFullPath(Option("--server")!), Option("--record")!, Option("--report")!, Option("--gate")!, Option("--release")!);
    return;
}
if (args.Contains("--local-hold-lock-child"))
{
    await LocalStartupChecks.HoldLockChildAsync(Path.GetFullPath(Option("--server")!), Option("--record")!);
    return;
}
if (args.Contains("--local-startup-checks"))
{
    await LocalStartupChecks.RunAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--local-incompatible-check"))
{
    await LocalStartupChecks.IncompatibleAsync(Path.GetFullPath(Option("--server")!));
    return;
}
if (args.Contains("--client-protocol-checks"))
{
    await ClientProtocolChecks.RunAsync();
    return;
}
if (Option("--deployment-wsl") is { } deploymentDistro)
{
    await DeploymentChecks.RunAsync(deploymentDistro, Path.GetFullPath(Option("--package") ?? "build/remote/sourcegit-server-linux-x64.tar.gz"));
    return;
}
if (Option("--wsl") is { } distro || Option("--ssh") != null)
{
    var kind = Option("--wsl") != null ? "Wsl" : "Ssh";
    var target = Option("--wsl") ?? Option("--ssh")!;
    var package = Path.GetFullPath(Option("--package") ?? "build/remote/sourcegit-server-linux-x64.tar.gz");
    var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(package)));
    // Remote operations must never touch the client filesystem (historical bug: literal server-path directories created client-side).
    var cwd = Directory.GetCurrentDirectory();
    var clientEntries = new HashSet<string>(Directory.GetFileSystemEntries(cwd), StringComparer.Ordinal);
    await using var manager = new ConnectionManager(new ConnectionOptions("integration-" + Guid.NewGuid().ToString("N"), "Integration", kind, target, PackagePath: package, PackageSha256: digest));
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var remote = await manager.ConnectAsync(deadline.Token);
    var environment = await remote.GetEnvironmentAsync();
    Console.WriteLine($"ENV {environment.OS}; {environment.Architecture}; {environment.GitVersion}");
    var root = "/tmp/sourcegit-connector-test-" + Guid.NewGuid().ToString("N");
    await remote.FileAsync(new FileRequest("", "mkdir", root));
    Success(await remote.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: root)));
    var repository = await remote.OpenRepositoryAsync(root);
    Success(await remote.ExecuteGitAsync(new GitRequest(repository.Id, ["status", "--porcelain=v1"])));
    var leaked = Directory.GetFileSystemEntries(cwd).Except(clientEntries).ToArray();
    Check(leaked.Length == 0, "Client working directory polluted by remote operations: " + string.Join(", ", leaked));
    Console.WriteLine("PASS " + kind + " actual deployment, handshake, empty repository and Git query");
    return;
}
var recordPath = Option("--record");
var serverDll = Option("--server");
string testRoot = Option("--root") ?? (serverDll != null ? Path.GetFullPath(Path.Combine("artifacts", "remote-tests", Guid.NewGuid().ToString("N"))) : "");
Process? server = null;
if (serverDll != null)
{
    Directory.CreateDirectory(testRoot);
    recordPath = Path.Combine(testRoot, "server.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(serverDll!)); start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
        server = Process.Start(start) ?? throw new Exception("Server did not start");
        _ = Task.Run(async () => { using var stdout = new FileStream(Path.Combine("artifacts", "last-server-stdout.log"), FileMode.Create); await server!.StandardOutput!.BaseStream.CopyToAsync(stdout); });
        _ = Task.Run(async () => { using var stderr = new FileStream(Path.Combine("artifacts", "last-server-stderr.log"), FileMode.Create); await server!.StandardError!.BaseStream.CopyToAsync(stderr); });
    for (var i = 0; i < 100 && !File.Exists(recordPath) && !server.HasExited; i++) await Task.Delay(100);
}
if (recordPath == null || !File.Exists(recordPath)) throw new Exception("Supply --server DLL or a readable --record JSON path");
var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath), RemoteJson.Default.ServerRecord)!;
var host = Option("--host") ?? "127.0.0.1";
var port = int.TryParse(Option("--port"), out var forwardedPort) ? forwardedPort : record.Port;
RemoteRepositoryClient? client = null;
try
{
    await Test("Reject invalid frame lengths", async () =>
    {
        foreach (var size in new[] { 0, -1, Protocol.MaxFrame + 1 })
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, size);
            await Throws<InvalidDataException>(async () => { await Protocol.ReadAsync(new MemoryStream(bytes)); });
        }
    });
    await Test("Reject unauthenticated client", async () =>
    {
        await using var unauthorized = new RemoteRepositoryClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Throws<IOException>(async () => { await unauthorized.ConnectAsync(host, port, "invalid", timeout.Token); });
    });
    client = new RemoteRepositoryClient();
    await Test("Authenticate and inspect environment", async () =>
    {
        var handshake = await client.ConnectAsync(host, port, record.Token);
        Check(handshake.InstanceId == record.InstanceId, "Unexpected server instance");
        var environment = await client.GetEnvironmentAsync();
        Check(environment.GitVersion.StartsWith("git version"), "Git executable unavailable");
        Console.WriteLine($"ENV {environment.OS}; {environment.Architecture}; {environment.GitVersion}");
    });
    await Test("Reject simultaneous second GUI owner", async () =>
    {
        await using var second = new RemoteRepositoryClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Throws<IOException>(async () => { await second.ConnectAsync(host, port, record.Token, timeout.Token); });
    });
    await Test("Same GUI owner can open a second channel", async () =>
    {
        await using var sibling = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await sibling.ConnectAsync(host, port, record.Token);
        Check((await sibling.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Second channel unavailable");
        Check((await client.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "First channel was displaced");
    });
    await Test("A new GUI owner can take over after the previous owner disconnects", async () =>
    {
        if (client == null) throw new Exception("No connected client");
        await client.DisposeAsync();
        await Task.Delay(500);
        await using var successor = new RemoteRepositoryClient();
        await successor.ConnectAsync(host, port, record.Token);
        Check((await successor.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Takeover owner unavailable");
        await using var displaced = new RemoteRepositoryClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Throws<IOException>(async () => { await displaced.ConnectAsync(host, port, record.Token, timeout.Token); });
        await successor.DisposeAsync();
        await Task.Delay(500);
        client = new RemoteRepositoryClient();
        await client.ConnectAsync(host, port, record.Token);
        Check((await client.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Original owner did not regain ownership");
    });
    if (serverDll != null) await Test("A new owner can take over after a silent owner channel expires", async () =>
    {
        if (serverDll == null) throw new Exception("Local server DLL required");
        // A crashed GUI behind broken WSL2 NAT forwarding leaves a channel whose peer never delivers
        // FIN/RST: the socket stays ESTABLISHED, so the read loop never sees EOF. The server must cut
        // such silent channels (a live client pings every 10 s) so ownership cannot pin the instance.
        var livenessRoot = Path.Combine(testRoot, "liveness");
        Directory.CreateDirectory(livenessRoot);
        var livenessRecord = Path.Combine(livenessRoot, "server.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(serverDll!)); start.ArgumentList.Add("--record"); start.ArgumentList.Add(livenessRecord);
        start.Environment["SOURCEGIT_CHANNEL_GRACE_SECONDS"] = "3";
        using var liveness = Process.Start(start) ?? throw new Exception("Liveness server did not start");
        try
        {
            _ = liveness.StandardOutput.ReadToEndAsync(); _ = liveness.StandardError.ReadToEndAsync();
            for (var i = 0; i < 100 && !File.Exists(livenessRecord) && !liveness.HasExited; i++) await Task.Delay(100);
            var livenessServer = JsonSerializer.Deserialize(await File.ReadAllTextAsync(livenessRecord), RemoteJson.Default.ServerRecord)!;
            // A silent owner: one handshake, then nothing (exactly what a dead peer leaves behind).
            using var ghost = new TcpClient();
            await ghost.ConnectAsync(host, livenessServer.Port);
            var ghostGate = new SemaphoreSlim(1);
            await Protocol.WriteAsync(ghost.GetStream(), new Envelope("g1", "Handshake", Protocol.Json(new HandshakeRequest(livenessServer.Token, "ghost-owner", "ghost-recovery"), RemoteJson.Default.HandshakeRequest)), ghostGate);
            Check(await Protocol.ReadAsync(ghost.GetStream()) is not null, "Ghost owner handshake failed");
            await using var blocked = new RemoteRepositoryClient("blocked-owner");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Throws<IOException>(async () => { await blocked.ConnectAsync(host, livenessServer.Port, livenessServer.Token, timeout.Token); });
            RemoteRepositoryClient? successor = null;
            var takenOver = false;
            for (var i = 0; i < 40 && !takenOver; i++)
            {
                var candidate = new RemoteRepositoryClient("successor-owner");
                try { await candidate.ConnectAsync(host, livenessServer.Port, livenessServer.Token, CancellationToken.None); successor = candidate; takenOver = true; }
                catch (IOException) { await candidate.DisposeAsync(); await Task.Delay(500); }
            }
            Check(takenOver, "Takeover did not succeed after the silent channel expired");
            Check((await successor!.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Takeover owner unavailable");
            await successor.ShutdownAsync();
            await successor.DisposeAsync();
        }
        finally
        {
            if (!liveness.HasExited) liveness.Kill(true);
            await liveness.WaitForExitAsync();
        }
    });
    if (string.IsNullOrEmpty(testRoot)) testRoot = (await client.GetEnvironmentAsync()).Home + "/sourcegit-remote-tests-" + Guid.NewGuid().ToString("N");
    var repoPath = testRoot.TrimEnd('/', '\\') + "/repo space 中文";
    RepositoryInfo? repo = null;
    await Test("Initialize and open genuine empty repository", async () =>
    {
        await client.FileAsync(new FileRequest("", "mkdir", repoPath));
        var init = await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: repoPath));
        Success(init);
        repo = await client.OpenRepositoryAsync(repoPath);
        Check(!repo.Bare && !string.IsNullOrEmpty(repo.CommonDirectory), "Invalid repository metadata");
        Success(await Git(["config", "user.name", "Remote Test"], false));
        Success(await Git(["config", "user.email", "remote-test@example.invalid"], false));
        Success(await Git(["config", "commit.gpgsign", "false"], false));
    });
    if (repo == null) throw new Exception("Repository fixture could not be created");
    await Test("Binary file transfer across chunk boundaries", async () =>
    {
        var bytes = new byte[2 * 1024 * 1024 + 19]; new Random(42).NextBytes(bytes);
        for (var offset = 0; offset < bytes.Length; offset += 1024 * 1024)
            await client.FileAsync(new FileRequest(repo.Id, "write", "binary data.bin", ContentBase64: Convert.ToBase64String(bytes, offset, Math.Min(1024 * 1024, bytes.Length - offset)), Offset: offset));
        var received = await client.ReadFileAsync(repo.Id, "binary data.bin");
        Check(bytes.SequenceEqual(received), "Binary bytes changed");
    });
    await Test("Conditional replacement rejects stale hashes and unmanaged uploads", async () =>
    {
        await Write("replacement.txt", "original\n");
        var originalHash = (await client.FileAsync(new FileRequest(repo.Id, "hash", "replacement.txt"))).Hash!;
        var temporary = (await client.FileAsync(new FileRequest(repo.Id, "tempfile", repo.WorktreePath))).Entries![0].Path;
        try
        {
            await client.FileAsync(new FileRequest("", "write", temporary, ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("replacement\n")), Truncate: true));
            await client.FileAsync(new FileRequest(repo.Id, "replace", "replacement.txt", Destination: temporary, ExpectedHash: originalHash));
            Check(Encoding.UTF8.GetString(await client.ReadFileAsync(repo.Id, "replacement.txt")) == "replacement\n", "Replacement content differs");
            await Throws<IOException>(async () => { await client.FileAsync(new FileRequest(repo.Id, "replace", "replacement.txt", Destination: temporary, ExpectedHash: originalHash)); });
            Check(Encoding.UTF8.GetString(await client.ReadFileAsync(repo.Id, "replacement.txt")) == "replacement\n", "Stale replacement changed target");
            var currentHash = (await client.FileAsync(new FileRequest(repo.Id, "hash", "replacement.txt"))).Hash!;
            await Throws<IOException>(async () => { await client.FileAsync(new FileRequest(repo.Id, "replace", "replacement.txt", Destination: repo.WorktreePath + "/replacement.txt", ExpectedHash: currentHash)); });
        }
        finally { await client.FileAsync(new FileRequest("", "delete", temporary)); }
    });
    await Test("Watcher publishes repository changes", async () =>
    {
        var changed = new TaskCompletionSource<RepositoryEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RepositoryEvent value) { if (value.RepositoryId == repo.Id) changed.TrySetResult(value); }
        client.RepositoryChanged += Handler;
        try
        {
            await Write("hello space 中文.txt", "initial\n");
            Check((await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Generation > 0, "Missing state generation");
        }
        finally { client.RepositoryChanged -= Handler; }
    });
    await Test("Stage, commit, history and diff match Git state", async () =>
    {
        Success(await Git(["add", "--", "hello space 中文.txt"], false));
        Success(await Git(["commit", "-m", "first integration commit"], false));
        Check(Text(await Git(["log", "-1", "--format=%s"])).Trim() == "first integration commit", "Missing commit");
        await Write("hello space 中文.txt", "initial\nmodified\n");
        Check(Text(await Git(["diff", "--", "hello space 中文.txt"])).Contains("+modified"), "Diff missing file modification");
    });
    await Test("Semantic status snapshot carries branch, changes and generation", async () =>
    {
        var head = Text(await Git(["rev-parse", "HEAD"])).Trim();
        var before = await client.GetStatusAsync(repo.Id);
        Check(before.Branch == "main" && before.HeadOid == head && before.Upstream == null, "Status branch header mismatch");
        var beforeChange = before.Changes.FirstOrDefault(c => c.Path == "hello space 中文.txt");
        Check(beforeChange is { IndexStatus: " ", WorkingTreeStatus: "M", Conflict: false }, "Status worktree change mismatch");
        Success(await Git(["add", "--", "hello space 中文.txt"], false));
        var afterStage = await client.GetStatusAsync(repo.Id);
        var stagedChange = afterStage.Changes.FirstOrDefault(c => c.Path == "hello space 中文.txt");
        Check(stagedChange is { IndexStatus: "M", WorkingTreeStatus: " " }, "Staged change mismatch");
        Success(await Git(["commit", "-m", "semantic status"], false));
        var afterCommit = await client.GetStatusAsync(repo.Id);
        Check(afterCommit.Generation > before.Generation, "Generation did not advance across writes");
        Check(afterCommit.Changes.All(c => c.Path != "hello space 中文.txt"), "Committed change still reported");
    });
    await Test("Semantic status preserves multiple changes, renames and untracked files", async () =>
    {
        var multiPath = testRoot + "/statusmulti " + Guid.NewGuid().ToString("N")[..8];
        await client.FileAsync(new FileRequest("", "mkdir", multiPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: multiPath)));
        var multiRepo = await client.OpenRepositoryAsync(multiPath);
        var git = (string[] arguments, bool readOnly = true) => client.ExecuteGitAsync(new GitRequest(multiRepo.Id, arguments, readOnly, readOnly ? null : Guid.NewGuid().ToString("N")));
        var write = (string file, string content) => client.FileAsync(new FileRequest(multiRepo.Id, "write", file, ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), Truncate: true));
        Success(await git(["config", "user.name", "Remote Test"], false));
        Success(await git(["config", "user.email", "remote-test@example.invalid"], false));
        await write("a.txt", "a\n");
        await write("b.txt", "b\n");
        Success(await git(["add", "-A"], false));
        Success(await git(["commit", "-m", "base"], false));
        await write("a.txt", "A\n");
        await write("b.txt", "B\n");
        await write("c.txt", "new\n");
        Success(await git(["mv", "b.txt", "renamed.txt"], false));
        var snapshot = await client.GetStatusAsync(multiRepo.Id);
        Check(snapshot.Changes.Length == 3, "Expected exactly three changes, got " + snapshot.Changes.Length);
        var plain = snapshot.Changes.FirstOrDefault(c => c.Path == "a.txt");
        Check(plain is { IndexStatus: " ", WorkingTreeStatus: "M", OriginalPath: null }, "Plain worktree change mismatch");
        var untracked = snapshot.Changes.FirstOrDefault(c => c.Path == "c.txt");
        Check(untracked is { IndexStatus: " ", WorkingTreeStatus: "?" }, "Untracked change mismatch");
        var renamed = snapshot.Changes.FirstOrDefault(c => c.Path == "renamed.txt");
        Check(renamed is { IndexStatus: "R", WorkingTreeStatus: "M", OriginalPath: "b.txt" }, "Rename original path mismatch: " + (renamed?.OriginalPath ?? "missing"));
        await client.CallAsync("CloseRepository", new IdRequest(multiRepo.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    });
    await Test("Semantic status reports tracking upstream and ahead/behind", async () =>
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var originPath = testRoot + "/track-origin" + suffix + ".git";
        await client.FileAsync(new FileRequest("", "mkdir", originPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--bare", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: originPath)));
        var workPath = testRoot + "/track-work " + suffix;
        await client.FileAsync(new FileRequest("", "mkdir", workPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["clone", "--no-hardlinks", originPath, workPath], false, Guid.NewGuid().ToString("N"), WorkingDirectory: testRoot)));
        var workRepo = await client.OpenRepositoryAsync(workPath);
        var git = (string[] arguments, bool readOnly = true) => client.ExecuteGitAsync(new GitRequest(workRepo.Id, arguments, readOnly, readOnly ? null : Guid.NewGuid().ToString("N")));
        var write = (string file, string content) => client.FileAsync(new FileRequest(workRepo.Id, "write", file, ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), Truncate: true));
        Success(await git(["config", "user.name", "Remote Test"], false));
        Success(await git(["config", "user.email", "remote-test@example.invalid"], false));
        await write("tracked.txt", "one\n");
        Success(await git(["add", "-A"], false));
        Success(await git(["commit", "-m", "one"], false));
        Success(await git(["push", "-u", "origin", "main"], false));
        var inSync = await client.GetStatusAsync(workRepo.Id);
        Check(inSync.Upstream == "origin/main" && inSync.Ahead == 0 && inSync.Behind == 0, "In-sync tracking header mismatch: upstream=" + (inSync.Upstream ?? "null") + " ahead=" + inSync.Ahead + " behind=" + inSync.Behind);
        await write("tracked.txt", "two\n");
        Success(await git(["commit", "-am", "two"], false));
        var ahead = await client.GetStatusAsync(workRepo.Id);
        Check(ahead.Ahead == 1 && ahead.Behind == 0, "Ahead count mismatch: " + ahead.Ahead + "/" + ahead.Behind);
        await client.CallAsync("CloseRepository", new IdRequest(workRepo.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    });
    await Test("Server rejects malformed frames and keeps serving", async () =>
    {
        // Oversized frame header: claims 5 MiB (> MaxFrame) — server must refuse without allocating.
        using (var bad = new System.Net.Sockets.TcpClient())
        {
            await bad.ConnectAsync("127.0.0.1", record.Port);
            var stream = bad.GetStream();
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, 5 * 1024 * 1024);
            await stream.WriteAsync(header);
            var read = new byte[1024];
            var got = await stream.ReadAsync(read.AsMemory());
            Check(got > 0, "Oversized frame header did not produce a rejection response");
        }
        // Non-JSON payload with a well-formed header: server must reject, not crash.
        using (var bad = new System.Net.Sockets.TcpClient())
        {
            await bad.ConnectAsync("127.0.0.1", record.Port);
            var stream = bad.GetStream();
            var payload = Encoding.ASCII.GetBytes("notjson");
            var frame = new byte[4 + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
            payload.CopyTo(frame, 4);
            await stream.WriteAsync(frame);
            var read = new byte[1024];
            var got = await stream.ReadAsync(read.AsMemory());
            Check(got > 0, "Malformed JSON frame did not produce a rejection response");
        }
        // The server itself must still answer on the established session and on a fresh handshake.
        var still = await client.GetStatusAsync(repo.Id);
        Check(still.Generation > 0, "Server stopped serving after malformed frames");
        using var probe = new System.Net.Sockets.TcpClient();
        await probe.ConnectAsync("127.0.0.1", record.Port);
        var probeStream = probe.GetStream();
        var probeRequest = new Envelope(Guid.NewGuid().ToString("N"), "Probe", Protocol.Json(new HandshakeRequest(record.Token, "malformed-probe", ""), RemoteJson.Default.HandshakeRequest));
        var probeBytes = JsonSerializer.SerializeToUtf8Bytes(probeRequest, RemoteJson.Default.Envelope);
        var probeFrame = new byte[4 + probeBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(probeFrame, probeBytes.Length);
        probeBytes.CopyTo(probeFrame, 4);
        await probeStream.WriteAsync(probeFrame);
        var probeHeader = new byte[4];
        await probeStream.ReadExactlyAsync(probeHeader);
        var probeLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(probeHeader);
        Check(probeLength > 4 && probeLength < 65536, "Probe handshake did not return a bounded reply");
        var probeBody = new byte[probeLength];
        await probeStream.ReadExactlyAsync(probeBody);
        var probeReply = JsonSerializer.Deserialize(probeBody, RemoteJson.Default.Envelope);
        Check(probeReply is { Error: null } && probeReply.Method == "Result", "Fresh handshake after malformed frames failed: " + (probeReply?.Error ?? "no reply"));
    });
    await Test("Remote submodule add and concurrent fresh-clone init work through the server", async () =>
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var innerPath = testRoot + "/sub-inner-" + suffix + ".git";
        await client.FileAsync(new FileRequest("", "mkdir", innerPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--bare", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: innerPath)));
        var outerPath = testRoot + "/sub-outer " + suffix;
        await client.FileAsync(new FileRequest("", "mkdir", outerPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: outerPath)));
        var outer = await client.OpenRepositoryAsync(outerPath);
        var op = (string[] arguments, bool readOnly = false) => client.ExecuteGitAsync(new GitRequest(outer.Id, arguments, readOnly, readOnly ? null : Guid.NewGuid().ToString("N")));
        Success(await op(["config", "user.name", "Remote Test"]));
        Success(await op(["config", "user.email", "remote-test@example.invalid"]));
        // Give the inner repo a commit so the submodule has a resolvable ref.
        await client.FileAsync(new FileRequest("", "mkdir", testRoot + "/sub-inner-seed " + suffix));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["clone", innerPath, "sub-inner-seed " + suffix], false, Guid.NewGuid().ToString("N"), WorkingDirectory: testRoot)));
        var seed = await client.OpenRepositoryAsync(testRoot + "/sub-inner-seed " + suffix);
        var seedOp = (string[] arguments, bool readOnly = false) => client.ExecuteGitAsync(new GitRequest(seed.Id, arguments, readOnly, readOnly ? null : Guid.NewGuid().ToString("N")));
        Success(await seedOp(["config", "user.name", "Remote Test"]));
        Success(await seedOp(["config", "user.email", "remote-test@example.invalid"]));
        await client.FileAsync(new FileRequest(seed.Id, "write", "inner.txt", ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("inner\n")), Truncate: true));
        Success(await seedOp(["add", "-A"]));
        Success(await seedOp(["commit", "-m", "inner seed"]));
        Success(await seedOp(["push", "origin", "main"]));
        await client.CallAsync("CloseRepository", new IdRequest(seed.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
        // The raw-channel shape the GUI produces after -c stripping (RemoteWorkspace.ExecuteGitAsync): clean verb + structured ConfigOverride.
        Success(await client.ExecuteGitAsync(new GitRequest(outer.Id, ["submodule", "add", innerPath, "inner"], false, Guid.NewGuid().ToString("N"), ConfigOverrides: ["protocol.file.allow=always"])));
        Success(await op(["commit", "-m", "add submodule"]));
        var status = Text(await op(["submodule", "status"], true));
        Check(status.Contains("inner"), "Submodule was not registered: " + status);
        // Fresh clone of the outer, then two concurrent submodule installs through the server: the per-common-directory
        // operation gate serializes them (first clones, second no-ops) — without it, concurrent git clones into the same
        // directory race (verified locally: one of two direct git invocations fails with "destination path already exists").
        var freshPath = testRoot + "/sub-fresh " + suffix;
        await client.FileAsync(new FileRequest("", "mkdir", freshPath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["clone", outerPath, freshPath], false, Guid.NewGuid().ToString("N"), WorkingDirectory: testRoot)));
        var fresh = await client.OpenRepositoryAsync(freshPath);
        var initA = client.ExecuteGitAsync(new GitRequest(fresh.Id, ["submodule", "update", "--init"], false, Guid.NewGuid().ToString("N"), ConfigOverrides: ["protocol.file.allow=always"]));
        var initB = client.ExecuteGitAsync(new GitRequest(fresh.Id, ["submodule", "update", "--init"], false, Guid.NewGuid().ToString("N"), ConfigOverrides: ["protocol.file.allow=always"]));
        var initResults = await Task.WhenAll(initA, initB);
        Check(initResults[0].ExitCode == 0 && initResults[1].ExitCode == 0, "Concurrent submodule init failed; A: " + initResults[0].Error + " B: " + initResults[1].Error);
        var innerResult = await client.FileAsync(new FileRequest(fresh.Id, "read", "inner/inner.txt"));
        // EOL may be normalized by core.autocrlf between commit and checkout; normalize before comparing.
        var innerText = innerResult.ContentBase64 == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(innerResult.ContentBase64)).Replace("\r\n", "\n");
        Check(innerText == "inner\n", "Submodule content mismatch after fresh-clone init; length=" + innerResult.Length + " base64=" + innerResult.ContentBase64);
        await client.CallAsync("CloseRepository", new IdRequest(fresh.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
    });
    await Test("Semantic refs snapshot lists branches and detects detached head", async () =>
    {
        var head = Text(await Git(["rev-parse", "HEAD"])).Trim();
        var refs = await client.GetRefsAsync(repo.Id);
        var main = refs.Refs.FirstOrDefault(r => r.Name == "refs/heads/main");
        Check(main is { Head: true, Upstream: "" } && main.ObjectId == head && main.CommitterDate > 0 && !refs.IsDetachedHead, "Refs snapshot mismatch");
        Success(await Git(["branch", "second"], false));
        var after = await client.GetRefsAsync(repo.Id);
        Check(after.Refs.Any(r => r.Name == "refs/heads/second"), "New branch missing from refs");
        var shortHead = Text(await Git(["rev-parse", "--short", "HEAD"])).Trim();
        Success(await Git(["checkout", "--detach", "HEAD"], false));
        var detached = await client.GetRefsAsync(repo.Id);
        Check(detached.IsDetachedHead && detached.DetachedOid == shortHead, "Detached head not reported");
        Success(await Git(["checkout", "main"], false));
        Check((await client.GetRefsAsync(repo.Id)).IsDetachedHead == false, "Detached head not cleared");
    });
    await Test("Shared repository handles release the watcher only after the last close", async () =>
    {
        var path = testRoot + "/refcount " + Guid.NewGuid().ToString("N")[..8];
        await client.FileAsync(new FileRequest("", "mkdir", path));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: path)));
        var first = await client.OpenRepositoryAsync(path);
        var second = await client.OpenRepositoryAsync(path);
        Check(first.WorktreePath == second.WorktreePath && first.Id == second.Id, "Same repository must share one server session");
        Check(!string.IsNullOrEmpty(first.LeaseId) && first.LeaseId != second.LeaseId, "Each open must return an independent lease");
        await client.CallAsync("CloseRepository", new IdRequest(first.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
        Check((await client.GetStatusAsync(first.Id)).Generation >= 0, "First close disposed the shared session");
        var changed = new TaskCompletionSource<RepositoryEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RepositoryEvent value) { if (value.RepositoryId == first.Id) changed.TrySetResult(value); }
        client.RepositoryChanged += Handler;
        try
        {
            await client.FileAsync(new FileRequest(first.Id, "write", "probe.txt", ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("x\n")), Truncate: true));
            Check((await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Generation > 0, "Watcher did not fire after reference drop");
        }
        finally { client.RepositoryChanged -= Handler; }
        await client.CallAsync("CloseRepository", new IdRequest(first.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
        Check((await client.GetStatusAsync(second.Id)).Generation >= 0, "Duplicate close released another user's lease");
        await client.CloseRepositoryAsync(second.LeaseId);
        await Throws<IOException>(async () => { await client.GetStatusAsync(first.Id); });
        var third = await client.OpenRepositoryAsync(path);
        Check(third.Id != first.Id, "Reopened repository must create a fresh session");
        Check((await client.GetStatusAsync(third.Id)).Generation >= 0, "Fresh session unusable");
        await client.CallAsync("CloseRepository", new IdRequest(third.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
        await client.FileAsync(new FileRequest("", "deleteTree", path));
    });
    await Test("Lost close acknowledgement is safely retried without releasing another lease", async () =>
    {
        var path = testRoot + "/lease-loss-" + Guid.NewGuid().ToString("N");
        await client.FileAsync(new FileRequest("", "mkdir", path));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: path)));
        var first = await client.OpenRepositoryAsync(path);
        var second = await client.OpenRepositoryAsync(path);
        await using var proxy = new DropReplyProxy(host, port, "CloseRepository");
        await using var interrupted = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await interrupted.ConnectAsync("127.0.0.1", proxy.Port, record.Token);
        await Throws<IOException>(() => interrupted.CloseRepositoryAsync(first.LeaseId));
        await proxy.Dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.CloseRepositoryAsync(first.LeaseId)));
        Check((await client.GetStatusAsync(second.Id)).Generation >= 0, "Retry after executed close released the remaining lease");
        await client.CloseRepositoryAsync(second.LeaseId);
        await Throws<IOException>(() => client.GetStatusAsync(second.Id));
        await client.CloseRepositoryAsync(second.LeaseId); // already expired is a successful no-op
        await client.FileAsync(new FileRequest("", "deleteTree", path));
    });
    if (serverDll != null) await Test("Repository leases enforce ownership, capacity and owner-session expiry", async () =>
    {
        using var service = new RepositoryService(maxLeases: 2);
        var first = await service.OpenAsync(repo.WorktreePath, "owner-a");
        var second = await service.OpenAsync(repo.WorktreePath, "owner-a");
        await Throws<IOException>(() => service.OpenAsync(repo.WorktreePath, "owner-a"));
        await Throws<UnauthorizedAccessException>(() => Task.Run(() => service.Close(first.LeaseId, "owner-b")));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => service.Close(first.LeaseId, "owner-a"))));
        Check(outcomes.Count(value => value != null) == 1 && service.Get(second.Id).Id == second.Id, "Concurrent close released more than once");
        var third = await service.OpenAsync(repo.WorktreePath, "owner-a");
        Check(third.Id == second.Id && third.LeaseId != second.LeaseId && third.LeaseId != first.LeaseId, "Released capacity was not reusable with a distinct lease");
        service.CloseAll();
        var nextOwner = await service.OpenAsync(repo.WorktreePath, "owner-b");
        Check(nextOwner.Id != second.Id && service.Close(second.LeaseId, "owner-a") == null, "Expired owner lease affected the new session");
        Check(service.Get(nextOwner.Id).Id == nextOwner.Id, "Expired release disposed a new owner's session");
        service.Close(nextOwner.LeaseId, "owner-b");
    });
    await Test("Protocol v1 clients are rejected before legacy repository-close semantics can run", async () =>
    {
        using var legacy = new TcpClient();
        await legacy.ConnectAsync(host, port);
        using var gate = new SemaphoreSlim(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Protocol.WriteAsync(legacy.GetStream(), new Envelope("legacy", "Handshake", Protocol.Json(
            new HandshakeRequest(record.Token, client.OwnerId, client.RecoveryToken, Version: 1), RemoteJson.Default.HandshakeRequest)), gate, deadline.Token);
        var response = await Protocol.ReadAsync(legacy.GetStream(), deadline.Token);
        Check(response?.Error?.Contains("Protocol/build mismatch") == true, "Legacy client was allowed to connect to lease protocol");
    });
    await Test("Semantic commit detail carries fields and decorations", async () =>
    {
        var head = Text(await Git(["rev-parse", "HEAD"])).Trim();
        var commit = await client.GetCommitAsync(repo.Id, "HEAD");
        Check(commit.Id == head, "Commit id mismatch");
        Check(commit.Parents.Length == 1, "Parent count mismatch");
        Check(commit.Subject == "semantic status", "Subject mismatch");
        Check(commit.Author == "Remote Test" && commit.AuthorEmail == "remote-test@example.invalid" && commit.AuthorTime > 0, "Author mismatch");
        Check(commit.Committer == "Remote Test" && commit.CommitTime >= commit.AuthorTime, "Committer mismatch");
        Check(commit.Decorations.Contains("HEAD -> main", StringComparison.Ordinal), "Missing HEAD decoration");
        var root = Text(await Git(["rev-list", "--max-parents=0", "HEAD"])).Trim();
        var first = await client.GetCommitAsync(repo.Id, root);
        Check(first.Id == root && first.Parents.Length == 0 && first.Subject == "first integration commit", "Root commit mismatch");
        Check(!first.Decorations.Contains("HEAD", StringComparison.Ordinal), "Root commit carries HEAD decoration");
        await Throws<IOException>(async () => { await client.GetCommitAsync(repo.Id, "0000000000000000000000000000000000000000"); });
    });
    await Test("Semantic blame maps lines to commits", async () =>
    {
        var root = Text(await Git(["rev-list", "--max-parents=0", "HEAD"])).Trim();
        var head = Text(await Git(["rev-parse", "HEAD"])).Trim();
        var data = await client.GetBlameAsync(repo.Id, "HEAD", "hello space 中文.txt");
        Check(!data.IsBinary, "Text file reported binary");
        Check(data.Lines.Length == 2, "Blame line count mismatch");
        // The root commit is a blame boundary (^ short sha), so all SHAs unify to the shortest length, matching the GUI parser.
        Check(data.Lines[0].CommitSHA == root[..7] && data.Lines[0].IsFirstInGroup, "First line must map to root commit");
        Check(data.Lines[1].CommitSHA == head[..7] && data.Lines[1].IsFirstInGroup, "Second line must map to head commit");
        Check(data.Lines[0].Author == "Remote Test" && data.Lines[0].Timestamp > 0, "Blame author/timestamp missing");
        Check(data.Content == "initial\nmodified", "Blame content mismatch");
        var atRoot = await client.GetBlameAsync(repo.Id, root, "hello space 中文.txt");
        Check(atRoot.Lines.Length == 1 && atRoot.Lines[0].CommitSHA == root[..7] && atRoot.Content == "initial", "Blame at root revision mismatch");
        var path = testRoot + "/blamebin " + Guid.NewGuid().ToString("N")[..8];
        await client.FileAsync(new FileRequest("", "mkdir", path));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: path)));
        var session = await client.OpenRepositoryAsync(path);
        try
        {
            await client.FileAsync(new FileRequest(session.Id, "write", "bin.dat", ContentBase64: Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }), Truncate: true));
            Success(await client.ExecuteGitAsync(new GitRequest(session.Id, ["add", "--all"], false, Guid.NewGuid().ToString("N"))));
            Success(await client.ExecuteGitAsync(new GitRequest(session.Id, ["commit", "-m", "binary"], false, Guid.NewGuid().ToString("N"))));
            var binary = await client.GetBlameAsync(session.Id, "HEAD", "bin.dat");
            Check(binary.IsBinary && binary.Lines.Length == 0, "Binary blame must be flagged");
        }
        finally
        {
            await client.CallAsync("CloseRepository", new IdRequest(session.LeaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
            await client.FileAsync(new FileRequest("", "deleteTree", path));
        }
    });



    await Test("Reject write incorrectly declared read-only", async () =>
    {
        await Throws<IOException>(async () => { await Git(["add", "--all"], true); });
    });
    await Test("Commit signinfo config override accepted, other values rejected", async () =>
    {
        Success(await client.ExecuteGitAsync(new GitRequest(repo.Id, ["show", "--no-show-signature", "--format=%G?", "-s", "HEAD"], true, ConfigOverrides: ["gpg.ssh.allowedSignersFile=/dev/null"])));
        await Throws<IOException>(async () => await client.ExecuteGitAsync(new GitRequest(repo.Id, ["show", "--no-show-signature", "--format=%G?", "-s", "HEAD"], true, ConfigOverrides: ["gpg.ssh.allowedSignersFile=/etc/passwd"])));
        await Throws<IOException>(async () => await client.ExecuteGitAsync(new GitRequest(repo.Id, ["show", "--no-show-signature", "--format=%G?", "-s", "HEAD"], true, ConfigOverrides: ["user.email=override@example.com"])));
    });
    await Test("Repeated operation ID executes commit once", async () =>
    {
        var operationId = Guid.NewGuid().ToString("N");
        var request = new GitRequest(repo.Id, ["commit", "--allow-empty", "-m", "deduplicated"], false, operationId);
        Success(await client.ExecuteGitAsync(request));
        var head = Text(await Git(["rev-parse", "HEAD"]));
        Success(await client.ExecuteGitAsync(request));
        Check(head == Text(await Git(["rev-parse", "HEAD"])), "Repeated commit changed HEAD");
        await Throws<IOException>(async () => { await client.ExecuteGitAsync(request with { Arguments = ["commit", "--allow-empty", "-m", "different"] }); });
    });
    await Test("Write outcome recoverable when acknowledgement is discarded", async () =>
    {
        var operationId = Guid.NewGuid().ToString("N");
        using (var transport = new TcpClient())
        using (var gate = new SemaphoreSlim(1))
        {
            await transport.ConnectAsync(host, port);
            var stream = transport.GetStream();
            var handshake = new HandshakeRequest(record.Token, client.OwnerId, client.RecoveryToken);
            await Protocol.WriteAsync(stream, new Envelope("fault-handshake", "Handshake", Protocol.Json(handshake, RemoteJson.Default.HandshakeRequest)), gate);
            var accepted = await Protocol.ReadAsync(stream);
            Check(accepted?.Error == null && accepted != null, "Fault-injection channel handshake failed");
            var write = new GitRequest(repo.Id, ["commit", "--allow-empty", "-m", "response discarded"], false, operationId);
            await Protocol.WriteAsync(stream, new Envelope("discard-this-response", "ExecuteGit", Protocol.Json(write, RemoteJson.Default.GitRequest)), gate);
            // Close without reading either acceptance or completion; no automatic request replay.
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        OperationInfo outcome;
        do { outcome = await client.GetOperationAsync(operationId, deadline.Token); if (outcome.State != "succeeded") await Task.Delay(100, deadline.Token); }
        while (outcome.State is "unknown" or "accepted" or "running");
        Check(outcome.State == "succeeded", "Discarded acknowledgement lost operation: " + outcome.State);
        Check(Text(await Git(["log", "-1", "--format=%s"])).Trim() == "response discarded", "Recovered result differs from repository state");
    });
    await Test("Disconnect after accepted write and recover result", async () =>
    {
        var owner = client.OwnerId; var recovery = client.RecoveryToken;
        var operationId = Guid.NewGuid().ToString("N");
        await client.CallAsync("ExecuteGit", new GitRequest(repo.Id, ["commit", "--allow-empty", "-m", "survives disconnect"], false, operationId), RemoteJson.Default.GitRequest, RemoteJson.Default.OperationInfo);
        await client.DisposeAsync(); await Task.Delay(250);
        client = new RemoteRepositoryClient(owner, recovery);
        await client.ConnectAsync(host, port, record.Token);
        OperationInfo operation;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do { operation = await client.GetOperationAsync(operationId, timeout.Token); if (operation.State is "accepted" or "running") await Task.Delay(100, timeout.Token); }
        while (operation.State is "accepted" or "running");
        Check(operation.State == "succeeded", "Lost accepted write outcome: " + operation.State);
        Check(Text(await Git(["log", "-1", "--format=%s"])).Trim() == "survives disconnect", "Write did not survive transport closure");
    });
    await Test("Missing operation reports unknown", async () => Check((await client.GetOperationAsync(Guid.NewGuid().ToString("N"))).State == "unknown", "Missing operation was not unknown"));
    await Test("File write recovers a discarded response without replaying CreateNew", async () =>
    {
        await using var proxy = new DropReplyProxy(host, port, "File");
        await using var faulted = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await faulted.ConnectAsync(host, proxy.Port, record.Token);
        faulted.RecoveryClient = _ => Task.FromResult(client);
        var request = new FileRequest(repo.Id, "write", "lost-file-reply.txt", ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("once\n")), CreateNew: true);
        var result = await faulted.FileAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.Length == 5, "Recovered file result missing");
        Check(Encoding.UTF8.GetString(await client.ReadFileAsync(repo.Id, request.Path)) == "once\n", "Recovered file content differs");
        Check(faulted.PendingOperations.Count == 0, "Resolved file operation remained pending");
    });
    await Test("Custom command recovers a discarded response and commits once", async () =>
    {
        var before = int.Parse(Text(await Git(["rev-list", "--count", "HEAD"])).Trim());
        await using var proxy = new DropReplyProxy(host, port, "ExecuteCustomCommand");
        await using var faulted = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await faulted.ConnectAsync(host, proxy.Port, record.Token);
        faulted.RecoveryClient = _ => Task.FromResult(client);
        Success(await faulted.ExecuteCustomAsync(new CustomCommandRequest(repo.Id, "git commit --allow-empty -m custom-response-lost", Guid.NewGuid().ToString("N"))).WaitAsync(TimeSpan.FromSeconds(10)));
        Check(int.Parse(Text(await Git(["rev-list", "--count", "HEAD"])).Trim()) == before + 1, "Custom command was replayed");
        Check(faulted.PendingOperations.Count == 0, "Resolved custom operation remained pending");
    });
    await Test("Shared command policy accepts ls-remote and keeps reflog writes tracked", async () =>
    {
        Check(GitCommandPolicy.IsQuery(["ls-remote", repo.WorktreePath]), "ls-remote misclassified");
        Check(!GitCommandPolicy.IsQuery(["reflog", "expire", "--all"]), "reflog mutation misclassified");
        var refs = Text(await Git(["ls-remote", repo.WorktreePath]));
        Check(refs.Contains("HEAD"), "ls-remote rejected or missing refs");
    });
    if (serverDll != null) await Test("Refs snapshot holds the common-directory lock across all Git queries", async () =>
    {
        using var repositories = new RepositoryService();
        var session = await repositories.OpenAsync(repo.WorktreePath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repositories.PrepareProcess = async (start, _) =>
        {
            if (start.ArgumentList.Contains("for-each-ref")) { entered.TrySetResult(); await release.Task; }
        };
        var snapshotTask = new RepositoryQueries(repositories).RefsAsync(new QueryRequest(session.Id), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checkout = repositories.ExecuteAsync(new GitRequest(session.Id, ["checkout", "--detach"], false));
        release.TrySetResult();
        var snapshot = await snapshotTask;
        Success(await checkout);
        Check(!snapshot.IsDetachedHead && snapshot.Refs.Any(x => x.Head), "Refs and HEAD were read on opposite sides of a checkout");
        Success(await repositories.ExecuteAsync(new GitRequest(session.Id, ["checkout", "main"], false)));
    });
    await Test("Repository relative traversal rejected", async () => { await Throws<IOException>(async () => { await client.FileAsync(new FileRequest(repo.Id, "read", "../outside.txt")); }); });
    await Test("Real Git editor helper returns client text", async () =>
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Handler(InteractionRequest interaction)
        {
            try
            {
                Check(interaction.Kind == "editor", "Unexpected helper interaction kind");
                await client.SubmitInteractionAsync(new InteractionReply(interaction.Id, true, "editor integration commit\n"));
                observed.TrySetResult(true);
            }
            catch (Exception ex) { observed.TrySetException(ex); }
        }
        client.InteractionRequested += Handler;
        try
        {
            Success(await Git(["commit", "--allow-empty"], false));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Text(await Git(["log", "-1", "--format=%s"])).Trim() == "editor integration commit", "Helper did not update commit message");
        }
        finally { client.InteractionRequested -= Handler; }
    });
    await Test("Editor survives reconnect and late interaction subscription; active shutdown rejected", async () =>
    {
        var operationId = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<InteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(InteractionRequest interaction) { if (interaction.OperationId == operationId) waiting.TrySetResult(interaction); }
        client.InteractionRequested += Observe;
        var oldClient = client;
        var recovered = new TaskCompletionSource<RemoteRepositoryClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldClient.RecoveryClient = _ => recovered.Task;
        var commit = oldClient.ExecuteGitAsync(new GitRequest(repo.Id, ["commit", "--allow-empty"], false, operationId));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Throws<IOException>(async () => { await oldClient.ShutdownAsync(); });
        oldClient.InteractionRequested -= Observe;
        var owner = oldClient.OwnerId; var recovery = oldClient.RecoveryToken;
        await oldClient.DisposeAsync();
        await Task.Delay(200);
        client = new RemoteRepositoryClient(owner, recovery);
        await client.ConnectAsync(host, port, record.Token);
        recovered.TrySetResult(client);
        await Task.Delay(300); // The replay deliberately arrives before the UI callback is bound.
        var replayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Respond(InteractionRequest interaction)
        {
            if (interaction.OperationId != operationId) return;
            try { await client.SubmitInteractionAsync(new InteractionReply(interaction.Id, true, "recovered editor commit\n")); replayed.TrySetResult(true); }
            catch (Exception ex) { replayed.TrySetException(ex); }
        }
        client.InteractionRequested += Respond;
        try
        {
            await replayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Success(await commit.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(Text(await Git(["log", "-1", "--format=%s"])).Trim() == "recovered editor commit", "Recovered editor did not complete original commit");
        }
        finally { client.InteractionRequested -= Respond; }
    });
    await Test("Merge conflict and abort preserve branch state", async () =>
    {
        Success(await Git(["restore", "--", "hello space 中文.txt"], false));
        var original = Text(await Git(["rev-parse", "HEAD"])).Trim();
        Success(await Git(["checkout", "-b", "conflicting"], false));
        await Write("hello space 中文.txt", "branch version\n");
        Success(await Git(["add", "--", "hello space 中文.txt"], false));
        Success(await Git(["commit", "-m", "branch change"], false));
        Success(await Git(["checkout", "main"], false));
        await Write("hello space 中文.txt", "main version\n");
        Success(await Git(["add", "--", "hello space 中文.txt"], false));
        Success(await Git(["commit", "-m", "main change"], false));
        var beforeMerge = Text(await Git(["rev-parse", "HEAD"]));
        var merge = await Git(["merge", "--no-edit", "conflicting"], false);
        Check(merge.ExitCode != 0, "Expected merge conflict");
        Check(Text(await Git(["ls-files", "-u"])).Length > 0, "Conflict index absent");
        Success(await Git(["merge", "--abort"], false));
        Check(Text(await Git(["rev-parse", "HEAD"])) == beforeMerge, "Abort changed HEAD");
        Check(Text(await Git(["ls-files", "-u"])).Length == 0, "Abort left conflicted index");
    });
    await Test("Linked worktree shares common directory", async () =>
    {
        var linkedPath = testRoot.TrimEnd('/', '\\') + "/linked-worktree";
        Success(await Git(["worktree", "add", "-b", "linked", linkedPath], false));
        var linked = await client.OpenRepositoryAsync(linkedPath);
        Check(linked.CommonDirectory == repo.CommonDirectory, "Linked worktree common directory differs");
        Check(linked.GitDirectory != repo.GitDirectory, "Linked worktree git directory must differ");
        Success(await client.ExecuteGitAsync(new GitRequest(linked.Id, ["status", "--porcelain=v1"])));
    });
    await Test("Bare repository opens without worktree assumptions", async () =>
    {
        var barePath = testRoot.TrimEnd('/', '\\') + "/bare.git";
        await client.FileAsync(new FileRequest("", "mkdir", barePath));
        Success(await client.ExecuteGitAsync(new GitRequest("", ["init", "--bare"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: barePath)));
        Check((await client.OpenRepositoryAsync(barePath)).Bare, "Bare repository was misidentified");
    });
    if (args.Contains("--slow")) await Test("Server auto-fetch respects disabled remotes without client fetch commands", async () =>
    {
        var originPath = testRoot.TrimEnd('/', '\\') + "/fetch-origin.git";
        Success(await client.ExecuteGitAsync(new GitRequest("", ["clone", "--bare", repoPath, originPath], false, Guid.NewGuid().ToString("N"), WorkingDirectory: testRoot)));
        Success(await Git(["remote", "add", "enabled", originPath], false));
        Success(await Git(["remote", "add", "disabled", originPath], false));
        Success(await Git(["config", "remote.disabled.disableautofetch", "true"], false));
        await client.ConfigureAutoFetchAsync(repo.Id, 1);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            while ((await Git(["show-ref", "--verify", "--quiet", "refs/remotes/enabled/main"])).ExitCode != 0)
                await Task.Delay(500, deadline.Token);
            Check((await Git(["show-ref", "--verify", "--quiet", "refs/remotes/disabled/main"])).ExitCode != 0, "Disabled remote was auto-fetched");
        }
        finally { await client.ConfigureAutoFetchAsync(repo.Id, 0); }
    }, 90);

    if (serverDll != null) await Test("Server replacement keeps an unresolved write unknown even when its ID exists again", async () =>
    {
        var operationId = Guid.NewGuid().ToString("N");
        await using var proxy = new DropReplyProxy(host, port, "File", dropCompletion: true);
        await using var faulted = new RemoteRepositoryClient(client.OwnerId, client.RecoveryToken);
        await faulted.ConnectAsync(host, proxy.Port, record.Token);
        var recovered = new TaskCompletionSource<RemoteRepositoryClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        faulted.RecoveryClient = _ => recovered.Task;
        var pending = faulted.FileAsync(new FileRequest(repo.Id, "write", "old-instance-write.txt", ContentBase64: "eA==", CreateNew: true, OperationId: operationId));
        await proxy.Dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.ShutdownAsync();
        await server!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        server.Dispose();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.GetFullPath(serverDll)); start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
        server = Process.Start(start)!;
        _ = server.StandardOutput.ReadToEndAsync(); _ = server.StandardError.ReadToEndAsync();
        for (var i = 0; i < 100 && !File.Exists(recordPath); i++) await Task.Delay(100);
        var replacement = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath!), RemoteJson.Default.ServerRecord)!;
        Check(replacement.InstanceId != record.InstanceId, "Server did not restart");
        await client.DisposeAsync();
        client = new RemoteRepositoryClient(faulted.OwnerId, faulted.RecoveryToken);
        await client.ConnectAsync(host, replacement.Port, replacement.Token);
        await client.FileAsync(new FileRequest("", "write", repo.WorktreePath + "/new-instance-write.txt", ContentBase64: "eQ==", CreateNew: true, OperationId: operationId));
        recovered.TrySetResult(client);
        await Throws<IOException>(async () => { await pending.WaitAsync(TimeSpan.FromSeconds(10)); });
        Check(faulted.PendingOperations.ContainsKey(operationId), "Unknown operation was incorrectly resolved using the new instance's record");
    });
    if (serverDll != null) await Test("Connection manager reuses its live local channel and remains Connected", async () =>
    {
        await using var manager = new ConnectionManager(new ConnectionOptions("reuse-" + Guid.NewGuid().ToString("N"), "Reuse regression", "Local", "", LocalServerPath: Path.GetFullPath(serverDll)));
        var first = await manager.ConnectAsync();
        var second = await manager.ConnectAsync();
        Check(ReferenceEquals(first, second) && manager.State == "Connected", "Live channel reuse left the manager Connecting");
        var invalidExposure = false;
        manager.StateChanged += state => { if (state == "Starting" && manager.Client != null) invalidExposure = true; };
        await manager.ShutdownForUpgradeAsync(first);
        Check(manager.Client == null && manager.State == "Starting" && !invalidExposure,
            "Accepted shutdown exposed the disposed client during upgrade");
    });
    if (serverDll != null) await Test("Connection manager retains its live client when upgrade is refused", async () =>
    {
        await using var manager = new ConnectionManager(new ConnectionOptions("refuse-" + Guid.NewGuid().ToString("N"), "Upgrade refusal", "Local", "", LocalServerPath: Path.GetFullPath(serverDll)));
        var live = await manager.ConnectAsync();
        var path = Path.Combine(testRoot, "upgrade-refusal");
        await live.FileAsync(new FileRequest("", "mkdir", path));
        Success(await live.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: path)));
        var handle = await live.OpenRepositoryAsync(path);
        Success(await live.ExecuteGitAsync(new GitRequest(handle.Id, ["config", "user.name", "Upgrade Test"], false)));
        Success(await live.ExecuteGitAsync(new GitRequest(handle.Id, ["config", "user.email", "test@example.invalid"], false)));
        var waiting = new TaskCompletionSource<InteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.InteractionRequested += request => waiting.TrySetResult(request);
        var commit = live.ExecuteGitAsync(new GitRequest(handle.Id, ["commit", "--allow-empty"], false));
        var interaction = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Throws<IOException>(() => manager.ShutdownForUpgradeAsync(live));
            Check(manager.Client == live && manager.State == "Connected" && !live.IsDisconnected,
                "Refused upgrade discarded the usable client");
            Check((await live.GetEnvironmentAsync()).GitVersion.StartsWith("git version"), "Refused-upgrade client cannot query");
        }
        finally { await live.SubmitInteractionAsync(new InteractionReply(interaction.Id, true, "finish active commit\n")); }
        Success(await commit.WaitAsync(TimeSpan.FromSeconds(10)));
        await manager.ShutdownForUpgradeAsync(live);
    });

    Task<GitResult> Git(string[] arguments, bool readOnly = true) => client.ExecuteGitAsync(new GitRequest(repo!.Id, arguments, readOnly, readOnly ? null : Guid.NewGuid().ToString("N")));
    Task<FileResult> Write(string path, string content) => client.FileAsync(new FileRequest(repo!.Id, "write", path, ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), Truncate: true));
}
catch (Exception ex) { results.Add(("Fixture", "FAIL", ex.Message)); Console.WriteLine("FAIL Fixture: " + ex.Message); }
finally
{
    if (client != null) await client.DisposeAsync();
    if (server != null) { var selfExited = server.HasExited; if (!selfExited) server.Kill(true); await server.WaitForExitAsync(); if (selfExited && server.ExitCode != 0) Console.WriteLine("NOTE server self-exited with code " + server.ExitCode + " (see artifacts/last-server-stderr.log)"); server.Dispose(); }
}
Console.WriteLine($"TOTAL {results.Count(x => x.Status == "PASS")} passed; {results.Count(x => x.Status == "FAIL")} failed");
Environment.ExitCode = results.Any(x => x.Status == "FAIL") ? 1 : 0;

string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
async Task Test(string name, Func<Task> test, int timeoutSeconds = 40)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); results.Add((name, "PASS", "")); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add((name, "FAIL", ex.Message)); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(ex.StackTrace); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Success(GitResult result) { Check(result.ExitCode == 0, $"Git exit {result.ExitCode}: {result.Error}"); }
static string Text(GitResult result) { Success(result); return Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
