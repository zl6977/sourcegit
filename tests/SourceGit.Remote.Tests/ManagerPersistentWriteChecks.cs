using System.IO.Pipes;
using System.Text;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ManagerPersistentWriteChecks
{
    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture uses the Windows Local connection path and a PowerShell named-pipe barrier");
        var id = "manager-write-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "sourcegit-manager-write-" + Guid.NewGuid().ToString("N"));
        var options = new ConnectionOptions(id, "Manager persistent write fixture", "Local", "", LocalServerPath: executable);
        var first = new ConnectionManager(options);
        var replacement = new ConnectionManager(options);
        var pipeName = "sourcegit-write-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        Task<GitResult>? write = null;
        string? operationId = null;
        RemoteRepositoryClient? cleanup = null;
        try
        {
            var client = await first.ConnectAsync(token);
            cleanup = client;
            var instance = client.InstanceId;
            await client.FileAsync(new FileRequest("", "mkdir", path), token);
            if ((await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: path), token)).ExitCode != 0) throw new Exception("Persistent fixture Git init failed");
            var repository = await client.OpenRepositoryAsync(path, token);
            operationId = Guid.NewGuid().ToString("N");
            var script = "$p = [IO.Pipes.NamedPipeClientStream]::new('.', '" + pipeName + "', [IO.Pipes.PipeDirection]::In); " +
                "$p.Connect(15000); if ($p.ReadByte() -ne 1) { throw 'Missing write release' }; " +
                "[IO.File]::AppendAllText('write-count.txt', 'once' + [Environment]::NewLine); $p.Dispose()";
            write = client.ExecuteCustomAsync(new CustomCommandRequest(repository.Id,
                "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), operationId), token);
            var pipeConnected = pipe.WaitForConnectionAsync(token);
            if (await Task.WhenAny(pipeConnected, write).WaitAsync(token) == write && !pipeConnected.IsCompleted)
            {
                var early = await write;
                throw new Exception("Persistent command finished before connecting its barrier: " + early.ExitCode + " " + early.Error);
            }
            await pipeConnected;
            if (!OperationState.IsActive((await client.GetOperationAsync(operationId, token)).State)) throw new Exception("Write was not registered and active at its actual command barrier");
            await first.DisposeAsync().AsTask().WaitAsync(token);
            cleanup = null;
            if (first.Client != null || first.State != "Disconnected") throw new Exception("Released manager exposed its disposed client");
            try { await write.WaitAsync(token); throw new Exception("Unreleased write returned a successful outcome on the disposed manager"); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
            var next = await replacement.ConnectAsync(token);
            cleanup = next;
            if (next.InstanceId != instance || !OperationState.IsActive((await next.GetOperationAsync(operationId, token)).State))
                throw new Exception("Manager disposal killed the server or its registered persistent write");
            var completion = next.CallAsync("WaitOperation", new IdRequest(operationId), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, token);
            if (completion.IsCompleted) throw new Exception("Unreleased persistent operation completed early");
            await pipe.WriteAsync(new byte[] { 1 }, token); await pipe.FlushAsync(token);
            var outcome = await completion.WaitAsync(token);
            if (outcome.State != "succeeded" || outcome.Result?.ExitCode != 0) throw new Exception("Independent persistent write did not finish after release: " + outcome.Error);
            var content = await next.FileAsync(new FileRequest(repository.Id, "read", "write-count.txt"), token);
            if (Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64!)).Replace("\r\n", "\n") != "once\n")
                throw new Exception("Manager disposal/recovery replayed the persistent write");
            await replacement.ShutdownForUpgradeAsync(next, token);
            cleanup = null;
            operationId = null;
            Console.WriteLine("PASS Disposing the actual ConnectionManager closes its waiter/handles while the same server keeps a registered write active; a new manager authoritatively waits for one successful write without replay");
        }
        finally
        {
            if (pipe.IsConnected) try { await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync(); } catch (IOException) { }
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (cleanup == null && operationId != null)
                try { cleanup = await replacement.ConnectAsync(cleanupDeadline.Token); } catch (Exception error) when (error is IOException or OperationCanceledException) { }
            if (cleanup is { IsDisconnected: false })
            {
                if (operationId != null) try { await cleanup.CallAsync("WaitOperation", new IdRequest(operationId), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, cleanupDeadline.Token); } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                try { await cleanup.ShutdownAsync(cleanupDeadline.Token); } catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            await first.DisposeAsync();
            await replacement.DisposeAsync();
            if (write != null) try { await write; } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
        }
    }
}
