using System.Diagnostics;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.Core;
using SourceGit.Server;
using SourceGit.RemoteClient;

internal static class ExecutionDeadlineChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static OperationStore.Operation NewOperation() => new() { Id = Guid.NewGuid().ToString("N"), Fingerprint = "test" };
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Active operation allowed shutdown/takeover");
    }
    public static async Task RunAsync()
    {
        var store = new OperationStore();
        var operation = store.Register("owner:id", "id", "test", out _);
        var entered = Signal(); var cleanup = Signal();
        var run = operation.ExecuteAsync(TimeSpan.FromMilliseconds(100), async token =>
        {
            Check(operation.EnterInteraction(), "Interaction rejected while running");
            Check(operation.EnterInteraction(), "Second helper rejected");
            operation.ExitInteraction();
            Check(operation.State == "waitingInteraction", "Parallel helper prematurely restored running");
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { operation.ExitInteraction(); await cleanup.Task; }
            return new(operation.Id, "succeeded");
        });
        await entered.Task.WaitAsync(Limit);
        await Until(() => operation.State == "terminating");
        operation.TerminationFailed(new IOException("injected termination failure"));
        Check(operation.Info().Error!.Contains("injected termination failure"), "Pending cleanup diagnostic missing");
        Reject(store.BeginShutdown); Reject(store.Clear);
        Check(!store.TryBeginIdleShutdown() && !run.IsCompleted, "Cleanup released idle/admission early");
        Check(!operation.EnterInteraction(), "New helper accepted while terminating");
        cleanup.SetResult(); await run.WaitAsync(Limit);
        Check(operation.State == "failed" && operation.Info().Error!.Contains("No rollback"), "Timeout outcome claims rollback");
        operation.ExitInteraction();
        Check(operation.State == "failed", "Helper rolled terminal state back");
        store.BeginShutdown();
        Console.WriteLine("PASS Terminating blocks shutdown, takeover and idle until cleanup; helpers cannot roll state back");

        var notificationFailure = NewOperation();
        var failureEntered = Signal();
        var failureCanceled = Signal();
        var failureCleanup = Signal();
        var failedNotificationRun = notificationFailure.ExecuteAsync(TimeSpan.FromSeconds(1), async token =>
        {
            notificationFailure.TerminationFailed(new IOException("native exit registration failed"));
            failureEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { failureCanceled.TrySetResult(); throw; }
            finally { await failureCleanup.Task; }
            return new(notificationFailure.Id, "succeeded");
        });
        try
        {
            await failureEntered.Task.WaitAsync(Limit);
            Check(notificationFailure.State == "terminating" && notificationFailure.Info().Error!.Contains("registration failed"), "Pre-deadline cleanup failure stayed running or lost diagnostics");
            await failureCanceled.Task.WaitAsync(Limit);
            Check(!failedNotificationRun.IsCompleted && !notificationFailure.Completion.IsCompleted, "Deadline skipped termination or released failed cleanup early");
            failureCleanup.TrySetResult();
            await failedNotificationRun.WaitAsync(Limit);
            Check(notificationFailure.State == "failed", "Failed notification cleanup did not publish a terminal failure");
            Console.WriteLine("PASS Pre-deadline notification failure enters terminating with diagnostics; the later deadline still cancels execution and awaits actual cleanup");
        }
        finally { failureCleanup.TrySetResult(); await failedNotificationRun.WaitAsync(Limit); }

        for (var i = 0; i < 100; i++)
        {
            var race = NewOperation();
            using var barrier = new Barrier(2);
            await Task.WhenAll(Task.Run(() => { barrier.SignalAndWait(); race.BeginTermination("deadline"); }), Task.Run(() => { barrier.SignalAndWait(); race.Complete(new(race.Id, "succeeded")); }));
            Check(race.State == "succeeded", "Completion/deadline race did not converge");
            Check(!race.Complete(new(race.Id, "failed")) && !race.BeginTermination("late"), "Terminal outcome published twice or rolled back");
        }
        Console.WriteLine("PASS 100 completion/deadline barrier races publish one terminal result without rollback");

        var success = NewOperation();
        await success.ExecuteAsync(TimeSpan.FromSeconds(2), async token =>
        {
            await Task.Delay(250, token); return new(success.Id, "succeeded");
        });
        Check(success.State == "succeeded", "Normal task timed out within configured limit");
        // A synchronous action may finish despite cancellation; preserve its actual result.
        var sync = NewOperation();
        await sync.ExecuteAsync(TimeSpan.FromMilliseconds(50), async token =>
        {
            await Task.Delay(150); return new(sync.Id, "succeeded");
        });
        Check(sync.State == "succeeded", "Completed noninterruptible write mislabeled as rolled back");
        Console.WriteLine("PASS Normal task and completed noninterruptible write retain their actual successful result");

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await Canceled(RepositoryService.RunAsync(Start("hang"), Convert.ToBase64String(new byte[4 * 1024 * 1024]), cancellation.Token));
        }
        Console.WriteLine("PASS Deadline covers blocked stdin and waits for process/output cleanup");

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await Canceled(RepositoryService.RunAsync(Start("orphan"), null, cancellation.Token));
        }
        Console.WriteLine("PASS Root exit with a descendant holding output open remains addressable until tree cleanup");

        var killFailed = Signal();
        Process? captured = null;
        var pending = NewOperation();
        var pendingRun = pending.ExecuteAsync(TimeSpan.FromSeconds(1), async token =>
        {
            var result = await RepositoryService.RunAsync(Start("hang"), null, token, terminationFailure: error => { pending.TerminationFailed(error); killFailed.TrySetResult(); }, terminate: process => { captured = process; throw new IOException("injected kill failure"); });
            return new(pending.Id, "succeeded", result);
        });
        await killFailed.Task.WaitAsync(Limit);
        Check(pending.State == "terminating" && !pendingRun.IsCompleted && pending.Info().Error!.Contains("injected kill failure"), "Failed kill published an early terminal result");
        captured!.Kill(true);
        await pendingRun.WaitAsync(Limit);
        Check(pending.State == "failed", "Cleanup after failed kill never finished");
        Console.WriteLine("PASS Injected process kill failure stays active and diagnostic until externally reaped");

        using var repositories = new RepositoryService();
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var initialized = await RepositoryService.RunAsync(RepositoryService.GitStart(root, ["init"]), null, default);
        Check(initialized.ExitCode == 0, "Fixture Git init failed");
        var repo = await repositories.OpenAsync(root);
        var commandStarted = Signal();
        repositories.PrepareProcess = (_, id) => { if (id == "lock-owner") commandStarted.TrySetResult(); return Task.CompletedTask; };
        using var ownerCancel = new CancellationTokenSource();
        var owner = repositories.ExecuteCustomAsync(repo.Id, OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30", "lock-owner", _ => { }, ownerCancel.Token);
        await commandStarted.Task.WaitAsync(Limit);
        var waiting = NewOperation();
        await waiting.ExecuteAsync(TimeSpan.FromMilliseconds(150), async token =>
        {
            var result = await repositories.FileAsync(new FileRequest(repo.Id, "write", Path.Combine(root, "never-written"), ContentBase64: "YQ=="), token);
            return new(waiting.Id, "succeeded", FileResult: result);
        });
        Check(waiting.State == "failed" && !File.Exists(Path.Combine(root, "never-written")) && !owner.IsCompleted, "Waiting deadline wrote or released another operation's lock");
        ownerCancel.Cancel(); await Canceled(owner);
        repositories.Close(repo.LeaseId);
        Console.WriteLine("PASS Deadline includes common-directory lock wait without writing or unlocking its owner");
        Console.WriteLine("TOTAL 8 PASS");
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    public static async Task ServerAsync(string serverPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-server-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "server.json");
        var start = new ProcessStartInfo(serverPath.EndsWith(".dll") ? "dotnet" : serverPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (serverPath.EndsWith(".dll")) start.ArgumentList.Add(serverPath);
        start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
        start.Environment["SOURCEGIT_OPERATION_TIMEOUT_SECONDS"] = "2";
        start.Environment["SOURCEGIT_GRACE_SECONDS"] = "2";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Until(() => File.Exists(recordPath) || process.HasExited);
            if (process.HasExited) throw new Exception("Server exited before ready: " + await stderr);
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath), RemoteJson.Default.ServerRecord)!;
            var owner = Guid.NewGuid().ToString("N"); var recovery = Guid.NewGuid().ToString("N");
            await using var first = new RemoteRepositoryClient(owner, recovery);
            await first.ConnectAsync("127.0.0.1", record.Port, record.Token);
            var path = Path.Combine(root, "repo"); Directory.CreateDirectory(path);
            await first.ExecuteGitAsync(new GitRequest("", ["init"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: path));
            var repo = await first.OpenRepositoryAsync(path);
            var normal = await first.ExecuteCustomAsync(new(repo.Id, OperatingSystem.IsWindows() ? "ping -n 2 127.0.0.1 >nul" : "sleep 1", "normal"));
            Check(normal.ExitCode == 0, "Normal command within configured deadline failed");
            Console.WriteLine("PASS Server configured execution deadline permits normal custom commands");
            var command = OperatingSystem.IsWindows() ? "echo partial>test.lock & ping -n 30 127.0.0.1 >nul" : "echo partial >test.lock; sleep 30";
            using var waitCancellation = new CancellationTokenSource();
            var waiting = first.ExecuteCustomAsync(new(repo.Id, command, "deadline-write"), waitCancellation.Token);
            await Until(() => File.Exists(Path.Combine(path, "test.lock")));
            try { await first.ShutdownAsync(); throw new Exception("Shutdown accepted running write"); } catch (IOException) { }
            await first.DisposeAsync();
            waitCancellation.Cancel();
            try { await waiting; } catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            await Task.Delay(200);
            await using var recovered = new RemoteRepositoryClient(owner, recovery);
            await recovered.ConnectAsync("127.0.0.1", record.Port, record.Token);
            Check(OperationState.IsActive((await recovered.GetOperationAsync("deadline-write")).State), "Disconnect terminated persistent write");
            OperationInfo info;
            using (var deadline = new CancellationTokenSource(Limit))
            {
                do { info = await recovered.GetOperationAsync("deadline-write", deadline.Token); if (OperationState.IsActive(info.State)) await Task.Delay(20, deadline.Token); } while (OperationState.IsActive(info.State));
            }
            Check(info.State == "failed" && info.Error!.Contains("Execution deadline") && info.Error.Contains("No rollback"), "Server timeout lost its partial-write diagnostic");
            Check(File.ReadAllText(Path.Combine(path, "test.lock")).Trim() == "partial", "Timeout removed partial writes/locks");
            Console.WriteLine("PASS Disconnect preserves persistent write; deadline reports partial outcome and keeps fixture lock");
            await recovered.ExecuteGitAsync(new GitRequest(repo.Id, ["config", "user.name", "Deadline Test"], false, "name"));
            await recovered.ExecuteGitAsync(new GitRequest(repo.Id, ["config", "user.email", "deadline@example.invalid"], false, "email"));
            var editing = recovered.ExecuteGitAsync(new GitRequest(repo.Id, ["commit", "--allow-empty"], false, "editor-timeout"));
            using (var deadline = new CancellationTokenSource(Limit))
            {
                do { info = await recovered.GetOperationAsync("editor-timeout", deadline.Token); if (info.State != "waitingInteraction") await Task.Delay(10, deadline.Token); } while (info.State != "waitingInteraction");
            }
            try { await recovered.ShutdownAsync(); throw new Exception("Shutdown accepted interaction"); } catch (IOException) { }
            try { await editing.WaitAsync(Limit); throw new Exception("Unanswered editor succeeded"); } catch (IOException error) { Check(error.Message.Contains("Execution deadline"), "Editor timeout diagnostic missing"); }
            info = await recovered.GetOperationAsync("editor-timeout");
            Check(info.State == "failed", "Helper rolled timeout back to running");
            Console.WriteLine("PASS Unanswered Git editor is terminated by execution deadline without helper state rollback");
            await recovered.CloseRepositoryAsync(repo.LeaseId);
            await recovered.ShutdownAsync(); await process.WaitForExitAsync().WaitAsync(Limit);
            Check(process.ExitCode == 0, "Server failed clean shutdown");
            Console.WriteLine("PASS Shutdown succeeds only after deadline tree/output cleanup reaches terminal state");
            Console.WriteLine("TOTAL 4 PASS");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await stdout; await stderr;
        }
    }
    private static async Task Canceled(Task task)
    {
        try { await task.WaitAsync(Limit); } catch (OperationCanceledException) { return; }
        throw new Exception("Deadline did not report cancellation");
    }
    private static ProcessStartInfo Start(string mode)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet") start.ArgumentList.Add(typeof(ExecutionDeadlineChecks).Assembly.Location);
        start.ArgumentList.Add("--execution-child"); start.ArgumentList.Add("--mode"); start.ArgumentList.Add(mode);
        return start;
    }
    public static async Task ChildAsync(string mode)
    {
        if (mode == "orphan")
        {
            await Task.Delay(400); // containment attached before deliberately orphaning
            var child = Start("hang"); child.RedirectStandardInput = false; child.RedirectStandardOutput = false; child.RedirectStandardError = false;
            using var launched = Process.Start(child)!;
            return;
        }
        Console.WriteLine("child-ready");
        await Task.Delay(TimeSpan.FromSeconds(40));
    }
}
