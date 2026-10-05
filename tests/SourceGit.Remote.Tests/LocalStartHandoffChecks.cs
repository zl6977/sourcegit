using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class LocalStartHandoffChecks
{
    internal static async Task ChildAsync(string path, string pipeName)
    {
        using var server = new RemoteServer(path);
        server.BeforeRecordPublished = async () =>
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(deadline.Token);
            var release = new byte[1];
            await pipe.ReadExactlyAsync(release, deadline.Token);
        };
        await server.RunAsync();
    }

    internal static async Task RunAsync()
    {
        foreach (var disposeCaller in new[] { false, true })
        {
            var id = "handoff-" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(Path.GetTempPath(), "sourcegit-server", id, "instance.json");
            var pipeName = "sourcegit-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var options = new ConnectionOptions(id, "Gated real server", "Local", "", LocalServerPath: typeof(LocalStartHandoffChecks).Assembly.Location);
            var first = new ConnectionManager(options);
            var second = new ConnectionManager(options);
            using var cancellation = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var spawned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = 0;
            first.ConfigureLocalLauncher = launcher => launcher.AfterProcessStarted = (child, _, _) => { spawned.TrySetResult(child.Id); return Task.CompletedTask; };
            first.Diagnostic += value => { if (value.Message.StartsWith("Started local server PID")) Interlocked.Increment(ref starts); };
            second.Diagnostic += value =>
            {
                if (value.Message.StartsWith("Started local server PID")) Interlocked.Increment(ref starts);
                if (value.Message.StartsWith("Waiting for local server startup decision lock")) contended.TrySetResult();
            };
            Task? disposing = null;
            Task<RemoteRepositoryClient>? discovery = null;
            Task<RemoteRepositoryClient>? reconnect = null;
            var original = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE");
            try
            {
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", pipeName);
                var context = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new NoPumpContext());
                    discovery = first.ConnectAsync(cancellation.Token);
                }
                finally { SynchronizationContext.SetSynchronizationContext(context); }
                var pid = await spawned.Task.WaitAsync(deadline.Token);
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", original);
                await pipe.WaitForConnectionAsync(deadline.Token);
                if (File.Exists(path)) throw new Exception("Fixture had already published record before cancellation");
                var elapsed = Stopwatch.StartNew();
                if (disposeCaller) disposing = first.DisposeAsync().AsTask();
                else cancellation.Cancel();
                try { await discovery.WaitAsync(deadline.Token); throw new Exception("Gated canceled connection succeeded"); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                if (elapsed.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception("Caller cancellation waited for readiness");
                disposing ??= first.DisposeAsync().AsTask();
                reconnect = second.ConnectAsync(deadline.Token);
                await contended.Task.WaitAsync(deadline.Token);
                if (reconnect.IsCompleted || disposing.IsCompleted || starts != 1) throw new Exception("Canceled owner abandoned its decision lock or startup handle");
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
                await pipe.FlushAsync(deadline.Token);
                var next = await reconnect.WaitAsync(deadline.Token);
                await disposing.WaitAsync(deadline.Token);
                var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, deadline.Token), RemoteJson.Default.ServerRecord)!;
                if (record.ProcessId != pid || next.InstanceId != record.InstanceId || starts != 1 || first.Client != null || first.State != "Disconnected")
                    throw new Exception("Canceled pre-publication startup was replaced or installed a late client");
                await second.ShutdownForUpgradeAsync(next, deadline.Token);
                Console.WriteLine("PASS " + (disposeCaller ? "Dispose cancels connection" : "Caller cancellation returns") + " promptly before real record publication; disposal joins owned readiness with retained lock/handles, then a competing manager reuses the sole original server");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SOURCEGIT_TEST_START_PIPE", original);
                if (pipe.IsConnected) try { await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync(); } catch (IOException) { }
                await first.DisposeAsync();
                if (second.Client is { IsDisconnected: false } client) try { await client.ShutdownAsync(); } catch (IOException) { }
                await second.DisposeAsync();
                if (discovery != null) try { await discovery; } catch (OperationCanceledException) { }
                if (reconnect != null) try { await reconnect; } catch (OperationCanceledException) { }
            }
        }
    }
    private sealed class NoPumpContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("Owned startup depended on a pumping UI context");
    }
}
