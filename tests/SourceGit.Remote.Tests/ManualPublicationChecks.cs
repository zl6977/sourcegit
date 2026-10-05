using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ManualPublicationChecks
{
    internal static async Task RunAsync()
    {
        await ObserverChecksAsync();
        foreach (var cancel in new[] { false, true }) await ManualAsync(cancel);
    }
    private static async Task ObserverChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "instance.json");
        var record = new ServerRecord(12345, "public-fixture-value", "fixture-instance", Environment.ProcessId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            async Task<ServerRecord?> Read(CancellationToken cancellation)
            {
                try { return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellation), RemoteJson.Default.ServerRecord); }
                catch (FileNotFoundException) { return null; }
            }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
            using (var observer = new RecordPublicationObserver(path))
                if (await observer.WaitAsync(Read, null, deadline.Token) != record || observer.Reads != 1) throw new Exception("Publication before subscription was lost");
            Console.WriteLine("PASS Publication before subscription is accepted by the immediate authoritative read");
            File.Delete(path);
            using (var observer = new RecordPublicationObserver(path))
            {
                var first = true;
                Task<ServerRecord?> Race(CancellationToken cancellation)
                {
                    if (!first) return Read(cancellation);
                    first = false;
                    // The current read saw absence; publication/overflow arrives
                    // before it returns that stale result. The signal must latch.
                    File.WriteAllText(path, JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
                    observer.NotifyError();
                    return Task.FromResult<ServerRecord?>(null);
                }
                if (await observer.WaitAsync(Race, null, deadline.Token) != record || observer.Reads != 2) throw new Exception("Notification during a stale read was lost");
            }
            Console.WriteLine("PASS Notification/overflow during a stale read stays latched and causes an authoritative reread");
            File.Delete(path);
            using (var observer = new RecordPublicationObserver(path))
            using (var canceled = new CancellationTokenSource())
            {
                observer.BeforeWait = () => canceled.Cancel();
                try { await observer.WaitAsync(Read, null, canceled.Token); throw new Exception("Publication wait ignored cancellation"); }
                catch (OperationCanceledException) { }
                if (observer.Reads != 1) throw new Exception("Idle record wait reread without a notification");
            }
            Console.WriteLine("PASS An absent record parks after one read and cancellation ends its notification wait");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task ManualAsync(bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "instance.json");
        var pipeName = "sourcegit-manual-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        var token = deadline.Token;
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--record", path }) info.ArgumentList.Add(arg);
        info.Environment["SOURCEGIT_TEST_START_PIPE"] = pipeName;
        using var child = Process.Start(info)!;
        _ = child.SafeHandle;
        var ready = child.StandardOutput.ReadLineAsync();
        var error = child.StandardError.ReadToEndAsync();
        await using var launcher = new LocalServerLauncher(Assembly.GetExecutingAssembly().Location, path);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        launcher.PublicationSubscribed = observer => observer.BeforeWait = () => waiting.TrySetResult();
        launcher.BeforeProcessStarted = _ => throw new Exception("Manual lifetime owner was ignored and a competing child was attempted");
        Task<ServerRecord>? discovery = null;
        RemoteRepositoryClient? client = null;
        try
        {
            await pipe.WaitForConnectionAsync(token);
            if (File.Exists(path) || File.Exists(path + ".startup.pending")) throw new Exception("Manual fixture had already published a record/intent");
            discovery = launcher.StartAsync((_, _) => throw new Exception("Unexpected manual upgrade"), cancellation.Token);
            await waiting.Task.WaitAsync(token);
            if (discovery.IsCompleted || child.HasExited) throw new Exception("Manual pending instance was replaced or its notification wait did not park");
            if (cancel)
            {
                cancellation.Cancel();
                try { await discovery.WaitAsync(token); throw new Exception("Manual publication cancellation succeeded"); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await launcher.DisposeAsync();
                if (child.HasExited || File.Exists(path)) throw new Exception("Cancel altered the live manual startup");
            }
            await pipe.WriteAsync(new byte[] { 1 }, token); await pipe.FlushAsync(token);
            if (!(await ready.WaitAsync(token))!.StartsWith("SOURCEGIT_READY ")) throw new Exception("Manual server did not publish ready");
            ServerRecord record;
            if (cancel)
            {
                await using var replacement = new LocalServerLauncher(Assembly.GetExecutingAssembly().Location, path);
                replacement.BeforeProcessStarted = _ => throw new Exception("Canceled manual discovery did not reuse the published server");
                record = await replacement.StartAsync((_, _) => throw new Exception("Unexpected upgrade"), token);
            }
            else record = await discovery.WaitAsync(token);
            if (record.ProcessId != child.Id) throw new Exception("Manual startup did not retain its original PID");
            client = new RemoteRepositoryClient();
            if ((await client.ConnectAsync("127.0.0.1", record.Port, record.Token, token)).InstanceId != record.InstanceId) throw new Exception("Manual record was not authenticated");
            await client.ShutdownAsync(token);
            await child.WaitForExitAsync(token);
            if (child.ExitCode != 0) throw new Exception("Manual server failed: " + await error);
            Console.WriteLine("PASS Real manual startup " + (cancel ? "survives canceled discovery and is reused on the next call" : "holds its lifetime lock without an intent; file notification discovers/authenticates its sole original PID") + " without a competing process or record polling");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            cancellation.Cancel();
            if (pipe.IsConnected) try { await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync(); } catch (IOException) { }
            await launcher.DisposeAsync();
            if (!child.HasExited) child.Kill(); await child.WaitForExitAsync();
            if (discovery != null) try { await discovery; } catch (OperationCanceledException) { }
        }
    }
}
