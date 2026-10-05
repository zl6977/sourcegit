using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ClientProtocolChecks
{
    private delegate Task Reply(JsonElement data, bool isEvent = false, string? eventMethod = null, string? error = null);
    public static async Task RunAsync()
    {
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ListDirectoryAsync("/"); throw new Exception("Missing RPC deadline"); }
            catch (TimeoutException error)
            {
                Check(error.Message.Contains("ListDirectory") && error.Message.Contains("seconds"), "Timeout omitted method/deadline");
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await client.ListDirectoryAsync("/", cancellation: cancellation.Token); throw new Exception("Caller cancellation ignored"); }
            catch (OperationCanceledException) { }
        }, TimeSpan.FromMilliseconds(200));
        Console.WriteLine("PASS RPC timeout includes method/deadline and preserves caller cancellation");

        var writes = 0;
        var queries = 0;
        var operationId = Guid.NewGuid().ToString("N");
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit") Interlocked.Increment(ref writes); // acknowledgement lost, channel stays alive
            else if (request.Method == "GetOperation")
            {
                Interlocked.Increment(ref queries);
                await reply(Protocol.Json(new OperationInfo(operationId, "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo));
            }
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            var result = await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, operationId));
            Check(result.ExitCode == 0 && writes == 1 && queries == 1, "Write timeout resent the request or lost its outcome");
        }, TimeSpan.FromMilliseconds(200));
        Console.WriteLine("PASS Write RPC timeout queries the operation without repeating the write");

        var unansweredId = Guid.NewGuid().ToString("N");
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, unansweredId)); throw new Exception("Unanswered write succeeded"); }
            catch (TimeoutException) { }
            Check(client.PendingOperations.ContainsKey(unansweredId), "Unknown write lost its recovery identity");
        }, TimeSpan.FromMilliseconds(200));
        Console.WriteLine("PASS Unanswered write/query deadlines retain the unknown operation identity");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var outputSent = false;
        try
        {
            await WithPeer(async (request, reply) =>
            {
                if (request.Method == "Handshake") await reply(Handshake());
                else if (request.Method == "GetOperation")
                {
                    if (!outputSent)
                    {
                        outputSent = true;
                        await reply(Protocol.Json(new OperationOutput("op", false, ""), RemoteJson.Default.OperationOutput), true);
                    }
                    await reply(Protocol.Json(new OperationInfo("op", "unknown"), RemoteJson.Default.OperationInfo));
                }
            }, async client =>
            {
                client.OperationOutput += _ => { entered.Set(); release.Wait(); };
                await client.ConnectAsync("127.0.0.1", _port, "token");
                var requests = Enumerable.Range(0, 4100).Select(_ => client.GetOperationAsync("op")).ToArray();
                Check(entered.Wait(TimeSpan.FromSeconds(5)), "Output callback never blocked");
                try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(15)); throw new Exception("Barrier backlog succeeded"); }
                catch (IOException error) { Check(error.Message.Contains("too slow"), "Unexpected backlog failure: " + error.Message); }
                Check(client.IsDisconnected && requests.All(x => x.IsCompleted), "Queue failure did not fail all requests");
                Check(requests.All(x => !x.IsCompletedSuccessfully), "Ordered reply overtook preceding output");
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                release.Set();
            });
        }
        finally { release.Set(); }
        Console.WriteLine("PASS ReplyBarrier backlog disconnects/fails pending requests in order; blocked callbacks do not hang disposal");

        var events = new List<string>();
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "GetOperation")
            {
                await reply(Protocol.Json(new OperationOutput("op", false, ""), RemoteJson.Default.OperationOutput), true);
                await reply(Protocol.Json(new OperationInfo("op", "succeeded"), RemoteJson.Default.OperationInfo));
            }
        }, async client =>
        {
            client.OperationOutput += _ => events.Add("output");
            await client.ConnectAsync("127.0.0.1", _port, "token");
            await client.GetOperationAsync("op");
            events.Add("reply");
            Check(events.SequenceEqual(new[] { "output", "reply" }), "Reply delivered before output");
        });
        Console.WriteLine("PASS Ordered operation replies follow preceding output events");

        var waits = 0;
        var pollingQueries = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit")
                await reply(Protocol.Json(new OperationInfo("long", "running"), RemoteJson.Default.OperationInfo));
            else if (request.Method == "GetOperation") Interlocked.Increment(ref pollingQueries);
            else if (request.Method == "WaitOperation")
            {
                Interlocked.Increment(ref waits);
                waitSeen.TrySetResult();
                await completed.Task;
                await reply(Protocol.Json(new OperationInfo("long", "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo));
            }
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            var operation = client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "long"));
            await waitSeen.Task;
            try
            {
                // This is a deadline test: exceed the ordinary RPC deadline while
                // the peer deliberately withholds both completion and broadcast.
                await Task.Delay(400);
                Check(!operation.IsCompleted, "Long wait used the ordinary RPC deadline");
            }
            finally { completed.TrySetResult(); }
            Check((await operation).ExitCode == 0 && waits == 1 && pollingQueries == 0,
                "Long wait restarted, polled, or depended on a completion broadcast");
        }, TimeSpan.FromMilliseconds(100));
        Console.WriteLine("PASS One WaitOperation survives the ordinary deadline and missing broadcast without polling");

        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "GetOperation") await reply(Protocol.Json(new OperationInfo("queued", "unknown"), RemoteJson.Default.OperationInfo));
            // Deliberately retain the original submit without acknowledging it.
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "queued")); throw new Exception("Unknown write succeeded"); }
            catch (IOException) { }
            Check(client.PendingOperations.ContainsKey("queued"), "Unknown query discarded the original queued write identity");
        }, TimeSpan.FromMilliseconds(100));
        Console.WriteLine("PASS Unknown query after submit timeout preserves the original pending write identity");

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredClient = new TaskCompletionSource<RemoteRepositoryClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistentWrites = 0;
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit")
            {
                persistentWrites++;
                await reply(Protocol.Json(new OperationInfo("dispose-write", "running"), RemoteJson.Default.OperationInfo));
            }
            else if (request.Method == "WaitOperation") parked.TrySetResult();
        }, async origin =>
        {
            await origin.ConnectAsync("127.0.0.1", _port, "token");
            origin.RecoveryClient = _ => recoveredClient.Task;
            var write = origin.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "dispose-write"));
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await origin.DisposeAsync();
            await WithPeer(async (request, reply) =>
            {
                if (request.Method == "Handshake") await reply(Handshake());
                else if (request.Method is "GetOperation" or "WaitOperation")
                    await reply(Protocol.Json(new OperationInfo("dispose-write", "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo));
                else if (request.Method == "ExecuteGit") throw new Exception("Recovery resubmitted the persistent write");
            }, async replacement =>
            {
                await replacement.ConnectAsync("127.0.0.1", _port, "token");
                recoveredClient.TrySetResult(replacement);
                Check((await write.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode == 0 && persistentWrites == 1,
                    "Disposing a channel canceled the persistent write instead of entering recovery");
            });
        });
        Console.WriteLine("PASS Channel disposal publishes disconnection before canceling a parked write wait and recovers without replay");

        var lateId = "late-registration";
        var lateWrites = 0;
        var lateQueries = 0;
        var registered = false;
        Reply? originalReply = null;
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit") { lateWrites++; originalReply = reply; }
            else if (request.Method == "GetOperation")
            {
                lateQueries++;
                await reply(Protocol.Json(new OperationInfo(lateId, registered ? "running" : "unknown"), RemoteJson.Default.OperationInfo));
            }
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, lateId)); throw new Exception("Unregistered submit succeeded"); }
            catch (IOException) { }
            Check(client.PendingOperations[lateId].State == "awaitingRegistration" && lateWrites == 1 && lateQueries == 1,
                "Unknown result lost its unregistered identity or started periodic queries");
            registered = true;
            await originalReply!(Protocol.Json(new OperationInfo(lateId, "running"), RemoteJson.Default.OperationInfo));
            Check((await client.GetOperationAsync(lateId)).State == "running" && client.PendingOperations[lateId].State == "registered", "Explicit query did not confirm late registration");
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OperationCompleted += info => { if (info.Id == lateId) delivered.TrySetResult(); };
            await originalReply(Protocol.Json(new OperationInfo(lateId, "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo), true, "OperationCompleted");
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!client.PendingOperations.ContainsKey(lateId) && lateWrites == 1 && lateQueries == 2, "Late terminal notification leaked its record or replayed the write");
        }, TimeSpan.FromMilliseconds(100));
        Console.WriteLine("PASS Unknown queued submit retains identity through late registration and terminal notification with one write and no periodic queries");

        var bounded = new OperationTracker(capacity: 1);
        var boundedWrites = 0;
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit") { boundedWrites++; await reply(Protocol.Json(new OperationInfo("bounded", "running"), RemoteJson.Default.OperationInfo)); }
            else if (request.Method == "WaitOperation") await reply(Protocol.Null, error: "Operation wait capacity reached");
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "bounded")); throw new Exception("Capacity error disappeared"); }
            catch (IOException error) { Check(error.Message.Contains("wait capacity"), "Wrong wait failure"); }
            Check(client.PendingOperations["bounded"].State == "registered", "Wait failure erased a registered persistent write");
            foreach (var id in new[] { "bounded", "overflow" })
                try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, id)); throw new Exception("Pending capacity/duplicate id was accepted"); }
                catch (InvalidOperationException) { }
            Check(boundedWrites == 1 && client.PendingOperations.Count == 1, "Capacity rejection occurred after submitting a write or evicted uncertainty");
        }, operations: bounded);
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Protocol.Json(new HandshakeResult("successor", Protocol.Version, Protocol.Build, []), RemoteJson.Default.HandshakeResult));
            else if (request.Method == "GetOperation") await reply(Protocol.Json(new OperationInfo("bounded", "succeeded"), RemoteJson.Default.OperationInfo));
        }, async successor =>
        {
            await successor.ConnectAsync("127.0.0.1", _port, "token");
            await successor.GetOperationAsync("bounded");
            Check(bounded.Pending["bounded"].State == "unknownInstance", "Successor reused ID resolved an old-instance write");
            Check(bounded.Forget("bounded") && !bounded.Forget("bounded") && bounded.Pending.Count == 0, "Explicit acknowledgement did not clear exactly one local record");
        }, operations: bounded);
        Console.WriteLine("PASS Wait failures retain writes, capacity rejects before submission, successor identity cannot resolve old writes, and explicit acknowledgement clears uncertainty");

        var bytes = new OperationTracker(byteCapacity: 1);
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit") throw new Exception("Oversized retention submitted a write");
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "too-large")); throw new Exception("Pending byte limit was ignored"); }
            catch (InvalidOperationException) { }
            Check(bytes.Pending.Count == 0, "Byte-capacity rejection retained a partial record");
        }, operations: bytes);
        Console.WriteLine("PASS Pending request byte budget rejects before network submission without evicting existing records");

        var beforeReplyWrites = 0;
        var beforeReplyQueries = 0;
        using var outputEntered = new ManualResetEventSlim();
        using var outputRelease = new ManualResetEventSlim();
        try
        {
            await WithPeer(async (request, reply) =>
            {
                if (request.Method == "Handshake") await reply(Handshake());
                else if (request.Method == "ExecuteGit")
                {
                    beforeReplyWrites++;
                    await reply(Protocol.Json(new OperationOutput("early-terminal", false, "YQ=="), RemoteJson.Default.OperationOutput), true);
                    await reply(Protocol.Json(new OperationInfo("early-terminal", "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo), true, "OperationCompleted");
                    await reply(Protocol.Json(new OperationInfo("early-terminal", "running"), RemoteJson.Default.OperationInfo));
                }
                else if (request.Method is "WaitOperation" or "GetOperation") { beforeReplyQueries++; throw new Exception("Known terminal event caused a redundant query"); }
            }, async client =>
            {
                client.OperationOutput += _ => { outputEntered.Set(); outputRelease.Wait(); };
                await client.ConnectAsync("127.0.0.1", _port, "token");
                var write = client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "early-terminal"));
                Check(outputEntered.Wait(TimeSpan.FromSeconds(5)) && !write.IsCompleted, "Early terminal overtook preceding output");
                outputRelease.Set();
                Check((await write.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode == 0 && beforeReplyWrites == 1 && beforeReplyQueries == 0 && client.PendingOperations.Count == 0,
                    "Early terminal was lost, queried again or leaked retained state");
            });
        }
        finally { outputRelease.Set(); }
        Console.WriteLine("PASS Terminal notification before submit reply retains its authoritative result, follows blocked output and avoids redundant waits");

        foreach (var cause in new[] { "deadline", "caller", "lifetime" })
        {
            var callerCancels = cause == "caller";
            var lifetimeCancels = cause == "lifetime";
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var classifyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClassify = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await WithPeer(async (request, reply) =>
                {
                    if (request.Method == "Handshake") await reply(Handshake());
                    else if (request.Method == "ListDirectory") throw new IOException("Injected channel EOF");
                }, async client =>
                {
                    client.BeforeDisconnectCancellation = () => { disconnectEntered.TrySetResult(); return releaseDisconnect.Task; };
                    client.BeforeRpcCancellationClassification = () => { classifyEntered.TrySetResult(); return releaseClassify.Task; };
                    client.Disconnected += _ => disconnected.TrySetResult();
                    await client.ConnectAsync("127.0.0.1", _port, "token");
                    using var caller = new CancellationTokenSource();
                    var request = client.ListDirectoryAsync("/", cancellation: caller.Token);
                    try
                    {
                        await disconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Check(client.IsDisconnected, "Disconnect barrier preceded its state publication");
                        await classifyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if (callerCancels) caller.Cancel();
                        if (lifetimeCancels) { releaseDisconnect.TrySetResult(); await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                        releaseClassify.TrySetResult();
                        try { await request; throw new Exception("Canceled RPC returned a result"); }
                        catch (TimeoutException error) when (!callerCancels && !lifetimeCancels) { Check(error.Message.Contains("ListDirectory"), "Deadline diagnostic lost its method"); }
                        catch (OperationCanceledException) when (callerCancels || lifetimeCancels) { }
                    }
                    finally { releaseClassify.TrySetResult(); releaseDisconnect.TrySetResult(); }
                }, TimeSpan.FromMilliseconds(200));
            }
            finally { releaseClassify.TrySetResult(); releaseDisconnect.TrySetResult(); }
        }
        Console.WriteLine("PASS Deadline with disconnected-state/lifetime gap remains a method timeout; caller or lifetime cancellation at the classification barrier remains cancellation");

        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ListDirectory") throw new IOException("Injected early EOF");
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            try { await client.ListDirectoryAsync("/"); throw new Exception("Failed connection returned a result"); }
            catch (IOException) { Check(client.IsDisconnected, "Connection failure was not published"); }
        }, TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS Connection failure completes the request before its deadline and preserves IOException instead of manufacturing a timeout");

        var authoritative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgedWrites = 0;
        await WithPeer(async (request, reply) =>
        {
            if (request.Method == "Handshake") await reply(Handshake());
            else if (request.Method == "ExecuteGit")
            {
                acknowledgedWrites++;
                await reply(Protocol.Json(new OperationInfo("known-before-loss", "succeeded", new GitResult(0, "", "")), RemoteJson.Default.OperationInfo), true, "OperationCompleted");
                // Lose the submit acknowledgement after delivering the authoritative completion.
            }
        }, async client =>
        {
            await client.ConnectAsync("127.0.0.1", _port, "token");
            client.RecoveryClient = _ => throw new Exception("Known completion waited for an unnecessary replacement channel");
            client.OperationCompleted += _ => authoritative.TrySetResult();
            var write = client.ExecuteGitAsync(new GitRequest("repo", ["fetch"], false, "known-before-loss"));
            await authoritative.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync();
            Check((await write.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode == 0 && acknowledgedWrites == 1 && client.PendingOperations.Count == 0,
                "Channel loss discarded an already known outcome or leaked its record");
        });
        Console.WriteLine("PASS Channel loss after authoritative completion returns the known result without reconnecting, resubmitting or retaining uncertainty");
    }

    private static int _port;
    private static JsonElement Handshake() => Protocol.Json(new HandshakeResult("instance", Protocol.Version, Protocol.Build, []), RemoteJson.Default.HandshakeResult);
    private static async Task WithPeer(Func<Envelope, Reply, Task> respond,
        Func<RemoteRepositoryClient, Task> check, TimeSpan? timeout = null, OperationTracker? operations = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(lifetime.Token);
            using var gate = new SemaphoreSlim(1);
            try
            {
                while (await Protocol.ReadAsync(socket.GetStream(), lifetime.Token) is { } request)
                    await respond(request, (data, isEvent, eventMethod, error) => Protocol.WriteAsync(socket.GetStream(),
                        new Envelope(isEvent ? "" : request.Id, isEvent ? eventMethod ?? "OperationOutput" : "Result", data, Error: error, Event: isEvent), gate, lifetime.Token));
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        });
        await using var client = new RemoteRepositoryClient(rpcTimeout: timeout, operations: operations);
        try { await check(client); }
        finally
        {
            lifetime.Cancel();
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
