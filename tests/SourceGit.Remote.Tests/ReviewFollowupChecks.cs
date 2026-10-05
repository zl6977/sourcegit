using System.Diagnostics;
using System.Reflection;
using SourceGit.Contracts;
using SourceGit.Core;
using SourceGit.RemoteClient;

internal static class ReviewFollowupChecks
{
    internal static async Task RunAsync()
    {
        var info = new byte[128];
        BitConverter.GetBytes(1234).CopyTo(info, 16);
        Check(LinuxGroupExitWaiter.HasExitedChild(info), "Root child PID was mistaken for UID");
        Array.Clear(info);
        BitConverter.GetBytes(1234).CopyTo(info, 20);
        Check(!LinuxGroupExitWaiter.HasExitedChild(info), "UID was mistaken for PID");
        Console.WriteLine("PASS 64-bit siginfo detects PID at 16 independently of UID at 20");

        foreach (var custom in new[] { false, true })
        {
            var root = Path.GetFullPath(Path.Combine("artifacts", "review-followup", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var init = await RepositoryService.RunAsync(RepositoryService.GitStart(root, ["init"]), null, default);
            Check(init.ExitCode == 0, "Git init failed");
            using var service = new RepositoryService();
            var repository = await service.OpenAsync(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.PrepareProcess = async (_, _) => { entered.TrySetResult(); await release.Task; };
            var task = custom
                ? service.ExecuteCustomAsync(repository.Id, "git config review.persisted yes", "review-write", _ => { })
                : service.ExecuteAsync(new GitRequest(repository.Id, ["config", "review.persisted", "yes"], ReadOnly: false, OperationId: "review-write"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(service.Close(repository.LeaseId) != null, "Last lease was not closed");
            }
            finally { release.TrySetResult(); }
            var result = await task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(result.ExitCode == 0 && result.OperationId == "review-write", "Successful persistent write lost its result after Close");
            var persisted = await RepositoryService.RunAsync(RepositoryService.GitStart(root, ["config", "--get", "review.persisted"]), null, default);
            Check(persisted.ExitCode == 0 && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(persisted.OutputBase64)).Trim() == "yes", "Write did not persist");
            Console.WriteLine($"PASS {(custom ? "Custom" : "Git")} write returns success after last lease closes while preparation is blocked");
        }

        using (var stream = new MemoryStream())
        using (var gate = new SemaphoreSlim(0, 1))
        using (var cancellation = new CancellationTokenSource())
        {
            var writing = Protocol.WriteAsync(stream, new Envelope("1", "event", Protocol.Null), gate, cancellation.Token, CancellationToken.None);
            cancellation.Cancel();
            try { await writing; throw new Exception("Queued canceled frame was written"); }
            catch (OperationCanceledException) { }
            Check(stream.Length == 0 && gate.CurrentCount == 0, "Canceled admission emitted bytes or released another writer's gate");
            Console.WriteLine("PASS Cancellation before frame admission emits no bytes");
        }
        using (var stream = new HeaderBarrierStream())
        using (var gate = new SemaphoreSlim(1, 1))
        using (var cancellation = new CancellationTokenSource())
        {
            var writing = Protocol.WriteAsync(stream, new Envelope("1", "event", Protocol.Null), gate, cancellation.Token, CancellationToken.None);
            await stream.Header.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            stream.Release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(10));
            await Protocol.WriteAsync(stream, new Envelope("2", "reply", Protocol.Null), gate);
            stream.Position = 0;
            Check((await Protocol.ReadAsync(stream))?.Id == "1" && (await Protocol.ReadAsync(stream))?.Id == "2", "Admitted cancellation damaged the following RPC frame");
            Console.WriteLine("PASS Cancellation after header preserves complete event and following RPC frame");
        }

        var serverRoot = Path.GetFullPath(Path.Combine("artifacts", "review-followup", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(serverRoot);
        using (var server = new RemoteServer(Path.Combine(serverRoot, "server.json")))
        {
            var running = server.RunAsync();
            try
            {
                var record = await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await using var client = new RemoteRepositoryClient();
                await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
                var channels = typeof(RemoteServer).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
                var keys = (System.Collections.IEnumerable)channels.GetType().GetProperty("Keys")!.GetValue(channels)!;
                var connection = keys.Cast<object>().Single();
                var gate = (SemaphoreSlim)connection.GetType().GetField("WriteGate")!.GetValue(connection)!;
                await gate.WaitAsync();
                try
                {
                    using var cancellation = new CancellationTokenSource();
                    var sending = (Task)typeof(RemoteServer).GetMethod("SendEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, ["RepositoryChanged", Protocol.Null, "", cancellation.Token])!;
                    cancellation.Cancel();
                    await sending.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { gate.Release(); }
                Check((await client.GetEnvironmentAsync()).GitVersion.Length > 0 && !client.IsDisconnected, "Canceled queued event closed the shared channel");
                await client.ShutdownAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine("PASS Actual server drops canceled queued event and serves the next RPC on the same channel");
            }
            finally { server.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        using (var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!)
        {
            await process.WaitForExitAsync();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            await using var manager = new ConnectionManager(new ConnectionOptions("review-drain", "Review drain", "local", "unused"));
            var drain = (Task)typeof(ConnectionManager).GetMethod("DrainTunnel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, [process])!;
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
            Check(drain.IsCompletedSuccessfully, "Disposed tunnel readers left an unobserved task failure");
            Console.WriteLine("PASS Disposed SSH readers complete drain without an unobserved exception");
        }
    }

    private sealed class HeaderBarrierStream : MemoryStream
    {
        internal readonly TaskCompletionSource Header = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 2) await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(bytes, cancellationToken);
            if (_writes == 1) Header.TrySetResult();
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
