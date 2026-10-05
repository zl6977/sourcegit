using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ServerShutdownChecks
{
    internal static async Task RunAsync()
    {
        foreach (var cancelReply in new[] { false, true })
        {
            var root = Path.GetFullPath(Path.Combine("artifacts", "shutdown-reply", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "server.json");
            using var server = new RemoteServer(path);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeReply = async (method, cancellation) =>
            {
                if (method != "Shutdown") return;
                reached.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            };
            var running = server.RunAsync();
            var record = await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RemoteRepositoryClient();
            await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
            using var caller = new CancellationTokenSource();
            var shutdown = client.ShutdownAsync(caller.Token);
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (running.IsCompleted || !File.Exists(path)) throw new Exception("Server stopped before sending the Shutdown reply");
                if (cancelReply)
                {
                    caller.Cancel();
                    try { await shutdown; throw new Exception("Canceled acknowledgement succeeded"); } catch (OperationCanceledException) { }
                }
                else
                {
                    release.TrySetResult();
                    if (await shutdown != "closing") throw new Exception("Shutdown acknowledgement was lost");
                }
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                if (File.Exists(path)) throw new Exception("Shutdown retained its owned record");
                Console.WriteLine(cancelReply
                    ? "PASS Accepted Shutdown still stops if reply wait is canceled, leaving acknowledgement unknown"
                    : "PASS Shutdown waits at a reply barrier, flushes acknowledgement, then stops without a fixed delay");
            }
            finally
            {
                release.TrySetResult();
                await client.DisposeAsync();
                server.Dispose();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (System.Net.Sockets.SocketException) { }
            }
        }
    }
}
