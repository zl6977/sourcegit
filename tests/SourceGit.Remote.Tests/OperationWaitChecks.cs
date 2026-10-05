using System.Diagnostics;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;
using SourceGit.Server;

internal static class OperationWaitChecks
{
    internal static async Task RunAsync(string executable)
    {
        var operation = new OperationStore.Operation { Id = "shared", Fingerprint = "check" };
        using var canceled = new CancellationTokenSource();
        var one = operation.Completion.WaitAsync(canceled.Token);
        var two = operation.Completion.WaitAsync(CancellationToken.None);
        canceled.Cancel();
        try { await one; throw new Exception("Canceled waiter succeeded"); } catch (OperationCanceledException) { }
        if (two.IsCompleted || operation.Completion.IsCompleted) throw new Exception("Wait cancellation canceled shared execution");
        if (!operation.Complete(new("shared", "succeeded")) || operation.Complete(new("shared", "failed")))
            throw new Exception("Completion was not published exactly once");
        if ((await two).State != "succeeded" || operation.Info().State != "succeeded") throw new Exception("Completion preceded saved terminal state");
        Console.WriteLine("PASS Shared completion preserves cancel isolation, saved terminal state, and exactly-once completion");
        await ShutdownDiagnosticsAsync();

        var root = Path.GetFullPath(Path.Combine("artifacts", "operation-wait-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "server.json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable); start.ArgumentList.Add("--record"); start.ArgumentList.Add(recordPath);
        start.Environment["SOURCEGIT_DEBUG_LOG"] = "1";
        using var server = Process.Start(start)!;
        var canceledCount = 0;
        var cancellationsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = Task.Run(async () =>
        {
            while (await server.StandardError.ReadLineAsync() is { } line)
                if (line.StartsWith("REQ WaitOperation ") && line.Contains("FAILED") && Interlocked.Increment(ref canceledCount) >= RemoteServer.OperationWaitersPerChannel)
                    cancellationsObserved.TrySetResult();
        });
        try
        {
            if (!(await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!.StartsWith("SOURCEGIT_READY ")) throw new Exception("Readiness failed");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(recordPath), RemoteJson.Default.ServerRecord)!;
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
            await client.ExecuteGitAsync(new GitRequest("", ["init", "--initial-branch=main"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: root));
            var repo = await client.OpenRepositoryAsync(root);
            var id = Guid.NewGuid().ToString("N");
            var command = OperatingSystem.IsWindows() ? "powershell -NoProfile -Command Start-Sleep -Seconds 5" : "sleep 5";
            await client.CallAsync("ExecuteCustomCommand", new CustomCommandRequest(repo.Id, command, id), RemoteJson.Default.CustomCommandRequest, RemoteJson.Default.OperationInfo);
            using var waitCancellation = new CancellationTokenSource();
            var waits = Enumerable.Range(0, RemoteServer.OperationWaitersPerChannel)
                .Select(_ => Wait(client, id, waitCancellation.Token)).ToArray();
            try { await Wait(client, id); throw new Exception("Wait capacity was not enforced"); }
            catch (IOException error) when (error.Message.Contains("wait capacity")) { }
            await client.CallAsync("Ping", "", RemoteJson.Default.String, RemoteJson.Default.String).WaitAsync(TimeSpan.FromSeconds(2));
            if (!OperationState.IsActive((await client.GetOperationAsync(id)).State)) throw new Exception("Operation finished before saturation test");
            Console.WriteLine("PASS Saturated wait lane rejects excess waits while Ping and GetOperation remain available");
            waitCancellation.Cancel();
            foreach (var wait in waits) { try { await wait; throw new Exception("Canceled wait succeeded"); } catch (OperationCanceledException) { } }
            await cancellationsObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var terminal = await Wait(client, id).WaitAsync(TimeSpan.FromSeconds(10));
            if (terminal.State != "succeeded") throw new Exception("Canceled waits affected persistent execution");
            if ((await Wait(client, id)).State != "succeeded" || (await Wait(client, "missing")).State != "unknown") throw new Exception("Terminal/unknown wait failed");
            await client.ShutdownAsync();
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await stderr;
            Console.WriteLine("PASS Cancel releases remote wait slots without canceling the operation; terminal and unknown waits return immediately");
        }
        finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
        await GlobalCapacityAsync();
        await QueuedWriteAsync();
    }

    private static async Task QueuedWriteAsync()
    {
        var limit = TimeSpan.FromSeconds(15);
        var root = Path.GetFullPath(Path.Combine("artifacts", "operation-queued-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var server = new RemoteServer(Path.Combine(root, "server.json"));
        var running = server.RunAsync();
        var record = await server.Ready.WaitAsync(limit);
        await using var proxy = new DropReplyProxy("127.0.0.1", record.Port, "__no_reply_drop__", dropCancellation: true);
        await using var client = new RemoteRepositoryClient(rpcTimeout: TimeSpan.FromSeconds(1));
        await client.ConnectAsync("127.0.0.1", proxy.Port, record.Token);
        await client.ExecuteGitAsync(new GitRequest("", ["init"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: root));
        var repository = await client.OpenRepositoryAsync(root);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        server.BeforeReply = async (method, _) =>
        {
            if (method != "GetEnvironment") return;
            if (Interlocked.Increment(ref entered) == 16) saturated.TrySetResult();
            await release.Task;
        };
        var holders = Enumerable.Range(0, 16).Select(_ => client.GetEnvironmentAsync()).ToArray();
        try
        {
            await saturated.Task.WaitAsync(limit);
            var before = proxy.WriteRequests;
            var id = Guid.NewGuid().ToString("N");
            var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OperationCompleted += info => { if (info.Id == id) terminal.TrySetResult(info); };
            try
            {
                await client.ExecuteCustomAsync(new(repository.Id,
                    OperatingSystem.IsWindows() ? "echo queued-once>>queued-count.txt" : "printf 'queued-once\\n' >>queued-count.txt", id));
                throw new Exception("Queued unregistered write returned a known result");
            }
            catch (IOException) { }
            if (!client.PendingOperations.TryGetValue(id, out var pending) || pending.State != "awaitingRegistration" || File.Exists(Path.Combine(root, "queued-count.txt")))
                throw new Exception("Unknown query failed to preserve a truly queued unregistered write");
            release.TrySetResult();
            var outcome = await terminal.Task.WaitAsync(limit);
            if (outcome.State != "succeeded" || client.PendingOperations.ContainsKey(id) || proxy.WriteRequests != before + 1 || File.ReadAllLines(Path.Combine(root, "queued-count.txt")).Length != 1)
                throw new Exception("Late real registration replayed a write, lost the terminal result or retained uncertainty");
            await client.CloseRepositoryAsync(repository.LeaseId);
            await client.ShutdownAsync();
            await running.WaitAsync(limit);
            Console.WriteLine("PASS A real write queued behind 16 RPC slots times out as unknown, registers later and writes exactly once when cancellation delivery is lost");
        }
        finally
        {
            release.TrySetResult();
            foreach (var holder in holders) try { await holder.WaitAsync(limit); } catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException) { }
            await client.DisposeAsync(); server.Dispose();
            await running.WaitAsync(limit);
        }
    }

    private static async Task GlobalCapacityAsync()
    {
        var limit = TimeSpan.FromSeconds(15);
        var root = Path.GetFullPath(Path.Combine("artifacts", "operation-global-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var server = new RemoteServer(Path.Combine(root, "server.json"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdReplies = 0;
        // Hold already-computed results before writing them: slots must remain owned until reply/cleanup.
        server.BeforeReply = (method, token) => method == "WaitOperation" && Volatile.Read(ref holdReplies) != 0 ? release.Task.WaitAsync(token) : Task.CompletedTask;
        var running = server.RunAsync();
        var record = await server.Ready.WaitAsync(limit);
        var owner = Guid.NewGuid().ToString("N");
        var recovery = Guid.NewGuid().ToString("N");
        var clients = new List<RemoteRepositoryClient>();
        var batches = new List<(CancellationTokenSource Cancellation, Task<OperationInfo>[] Waits)>();
        InteractionRequest? activeInteraction = null;
        RemoteRepositoryClient? editorClient = null;
        async Task<RemoteRepositoryClient> Connect()
        {
            var client = new RemoteRepositoryClient(owner, recovery);
            clients.Add(client);
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token).WaitAsync(limit);
            return client;
        }
        void Fill(RemoteRepositoryClient client)
        {
            var cancellation = new CancellationTokenSource();
            batches.Add((cancellation, Enumerable.Range(0, RemoteServer.OperationWaitersPerChannel)
                .Select(_ => Wait(client, "missing", cancellation.Token)).ToArray()));
        }
        async Task Count(int expected)
        {
            // Test-only bounded observation of cleanup; product waiting remains event driven.
            using var deadline = new CancellationTokenSource(limit);
            while (server.ActiveOperationWaiters != expected) await Task.Delay(10, deadline.Token);
        }
        async Task CapacityError(RemoteRepositoryClient client)
        {
            try { await Wait(client, "missing").WaitAsync(limit); throw new Exception("Global wait capacity was not enforced"); }
            catch (IOException error) when (error.Message.Contains("wait capacity")) { }
        }
        async Task Observe(Task<OperationInfo>[] waits)
        {
            foreach (var wait in waits)
                try { await wait.WaitAsync(limit); } catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        try
        {
            var channels = RemoteServer.OperationWaitersGlobal / RemoteServer.OperationWaitersPerChannel;
            editorClient = await Connect();
            if ((await editorClient.ExecuteGitAsync(new GitRequest("", ["init"], false, Guid.NewGuid().ToString("N"), WorkingDirectory: root))).ExitCode != 0)
                throw new Exception("Global editor fixture init failed");
            var repository = await editorClient.OpenRepositoryAsync(root);
            foreach (var config in new[] { new[] { "config", "user.name", "Capacity Check" }, new[] { "config", "user.email", "capacity@example.invalid" } })
                if ((await editorClient.ExecuteGitAsync(new GitRequest(repository.Id, config, false, Guid.NewGuid().ToString("N")))).ExitCode != 0)
                    throw new Exception("Global editor fixture config failed");
            var interaction = new TaskCompletionSource<InteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            editorClient.InteractionRequested += request => interaction.TrySetResult(request);
            await editorClient.CallAsync("ExecuteGit", new GitRequest(repository.Id, ["commit", "--allow-empty"], false, Guid.NewGuid().ToString("N")), RemoteJson.Default.GitRequest, RemoteJson.Default.OperationInfo);
            activeInteraction = await interaction.Task.WaitAsync(limit);
            if (activeInteraction.Kind != "editor") throw new Exception("Capacity fixture did not enter editor interaction");
            Volatile.Write(ref holdReplies, 1);
            Fill(editorClient);
            for (var index = 1; index < channels; index++) Fill(await Connect());
            await Count(RemoteServer.OperationWaitersGlobal);
            var spare = await Connect();
            await CapacityError(spare);
            await Count(RemoteServer.OperationWaitersGlobal);
            await spare.CallAsync("Ping", "", RemoteJson.Default.String, RemoteJson.Default.String).WaitAsync(limit);
            if ((await spare.GetOperationAsync("missing").WaitAsync(limit)).State != "unknown") throw new Exception("Global saturation blocked ordinary lookup");
            try { await spare.SubmitInteractionAsync(new("missing", false, "")).WaitAsync(limit); throw new Exception("Invalid interaction was accepted"); }
            catch (IOException error) when (error.Message.Contains("Interaction expired")) { }
            Console.WriteLine("PASS 1024 waits across 16 channels enforce global capacity while a spare channel can Ping, query and submit interaction replies");
            if (await spare.SubmitInteractionAsync(new(activeInteraction.Id, true, "capacity editor commit\n")).WaitAsync(limit) != "accepted")
                throw new Exception("Global saturation rejected a valid interaction reply");
            activeInteraction = null;
            var committed = await spare.ExecuteGitAsync(new GitRequest(repository.Id, ["log", "-1", "--format=%s"])).WaitAsync(limit);
            if (committed.ExitCode != 0 || System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(committed.OutputBase64)).Trim() != "capacity editor commit")
                throw new Exception("Global saturation prevented the real editor commit from completing");
            Console.WriteLine("PASS A real Git editor reply and the resulting commit complete while all 1024 wait-reply slots remain occupied");

            batches[0].Cancellation.Cancel();
            await Observe(batches[0].Waits);
            await Count(RemoteServer.OperationWaitersGlobal - RemoteServer.OperationWaitersPerChannel);
            Fill(spare);
            await Count(RemoteServer.OperationWaitersGlobal);
            await clients[1].DisposeAsync();
            await Observe(batches[1].Waits);
            await Count(RemoteServer.OperationWaitersGlobal - RemoteServer.OperationWaitersPerChannel);
            var replacement = await Connect();
            Fill(replacement);
            await Count(RemoteServer.OperationWaitersGlobal);
            await CapacityError(clients[0]);
            await Count(RemoteServer.OperationWaitersGlobal);
            Console.WriteLine("PASS Cancellation and disconnected reply waiters release exactly one channel's capacity; reconnect reuses the reclaimed global slots");

            foreach (var batch in batches) batch.Cancellation.Cancel();
            foreach (var batch in batches) await Observe(batch.Waits);
            await Count(0);
            Fill(replacement);
            await Count(RemoteServer.OperationWaitersPerChannel);
            release.TrySetResult();
            var results = await Task.WhenAll(batches[^1].Waits).WaitAsync(limit);
            if (results.Any(result => result.State != "unknown")) throw new Exception("Held results changed across capacity reclamation");
            await Count(0);
            var writingReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var channelClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Deliberately ignore cancellation at this barrier, so the real Reply attempts to write a closed stream.
            server.BeforeReply = async (method, _) =>
            {
                if (method != "WaitOperation") return;
                writingReply.TrySetResult();
                await finishReply.Task;
            };
            server.AfterChannelClosed = () => channelClosed.TrySetResult();
            server.RequestFailed = (method, error) => { if (method == "WaitOperation") writeFailure.TrySetResult(error); };
            var failedWriter = await Connect();
            var failedWait = Wait(failedWriter, "missing");
            try
            {
                await writingReply.Task.WaitAsync(limit);
                await failedWriter.DisposeAsync();
                await channelClosed.Task.WaitAsync(limit);
                if (server.ActiveOperationWaiters != 1) throw new Exception("Reply slot released before its failed write finished");
                finishReply.TrySetResult();
                var error = await writeFailure.Task.WaitAsync(limit);
                if (error is not (IOException or ObjectDisposedException)) throw new Exception("Fixture did not reach an actual closed-stream Reply write: " + error);
                await Observe([failedWait]);
                await Count(0);
            }
            finally { finishReply.TrySetResult(); await Observe([failedWait]); }
            server.BeforeReply = null; server.AfterChannelClosed = null; server.RequestFailed = null;
            if ((await Wait(replacement, "missing").WaitAsync(limit)).State != "unknown") throw new Exception("Failed reply leaked global wait capacity");
            await Count(0);
            Console.WriteLine("PASS Actual Reply write to a closed channel fails while retaining its slot until finally, then releases capacity for a new request");
            await replacement.ShutdownAsync().WaitAsync(limit);
            await running.WaitAsync(limit);
            Console.WriteLine("PASS Complete cancellation drains global capacity to zero; new replies complete and reclaim all slots before shutdown");
        }
        finally
        {
            release.TrySetResult();
            if (activeInteraction != null && editorClient != null && !editorClient.IsDisconnected)
                try { await editorClient.SubmitInteractionAsync(new(activeInteraction.Id, false, "")).WaitAsync(limit); }
                catch (IOException) { } // Preserve the original failure if the helper already expired.
            foreach (var batch in batches) batch.Cancellation.Cancel();
            foreach (var client in clients) await client.DisposeAsync();
            foreach (var batch in batches) { await Observe(batch.Waits); batch.Cancellation.Dispose(); }
            server.Dispose();
            await running.WaitAsync(limit);
        }
    }

    private static async Task ShutdownDiagnosticsAsync()
    {
        var store = new OperationStore();
        var accepted = store.Register("owner:accepted", "accepted", "test", out _);
        string Refusal()
        {
            try { store.BeginShutdown(); throw new Exception("Shutdown admitted active operations"); }
            catch (InvalidOperationException error) { return error.Message; }
        }
        if (Refusal().Contains("cleanup")) throw new Exception("Accepted operation was reported as failed cleanup");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = store.Register("owner:helper", "helper", "test", out _);
        var running = helper.ExecuteAsync(TimeSpan.FromSeconds(30), async _ =>
        {
            if (!helper.EnterInteraction()) throw new Exception("Interaction fixture failed to start");
            entered.TrySetResult();
            await release.Task;
            helper.ExitInteraction();
            return new(helper.Id, "succeeded");
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Refusal().Contains("cleanup")) throw new Exception("Waiting helper was reported as failed cleanup");
            accepted.BeginTermination("injected cleanup");
            accepted.TerminationFailed(new IOException(new string('x', 10000) + accepted.HelperToken));
            var refusal = Refusal();
            if (!refusal.Contains("termination/cleanup") || !refusal.Contains("GetOperation") || refusal.Length > 256 || refusal.Contains(accepted.HelperToken))
                throw new Exception("Mixed-state shutdown refusal is unbounded, exposes credentials or loses cleanup guidance");
            var afterRefusal = store.Register("owner:after-refusal", "after-refusal", "test", out var fresh);
            if (!fresh) throw new Exception("Refusal closed operation admission");
            afterRefusal.Complete(new(afterRefusal.Id, "succeeded"));
            if (store.TryBeginIdleShutdown()) throw new Exception("Cleanup/interaction allowed idle shutdown");
            accepted.Complete(new(accepted.Id, "failed", Error: "cleanup completed"));
            if (Refusal().Contains("cleanup")) throw new Exception("Reaped operation remained in cleanup diagnostic");
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            store.BeginShutdown();
            Console.WriteLine("PASS Mixed accepted/interaction/terminating shutdown diagnostics stay bounded and credential-free, preserve admission after refusal and clear only after terminal cleanup");
        }
        finally { release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    private static Task<OperationInfo> Wait(RemoteRepositoryClient client, string id, CancellationToken cancellation = default)
        => client.CallAsync("WaitOperation", new IdRequest(id), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, cancellation);
}
