using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

/// <summary>Serializes discovery/start decisions independently of the server's lifetime lock.</summary>
internal sealed class LocalServerLauncher : IDisposable, IAsyncDisposable
{
    private readonly string _executable;
    private readonly string _recordPath;
    private readonly Func<ServerRecord, CancellationToken, Task<HandshakeResult>> _probe;
    private readonly Action<string>? _diagnostic;
    private Process? _process;
    private Task<string>? _stdout, _stderr;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _activeStarts;
    private TaskCompletionSource _startsDrained = CompletedDrain();
    private bool _disposed, _cancellationComplete;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _readyTimeout;
    internal Func<CancellationToken, Task>? BeforeProcessStarted { get; set; }
    internal Func<Process, Task<string>, CancellationToken, Task>? AfterProcessStarted { get; set; }
    internal Func<Process, CancellationToken, Task>? BeforeChildBound { get; set; }
    internal Action<RecordPublicationObserver>? PublicationSubscribed { get; set; }

    internal LocalServerLauncher(string executable, string recordPath, Action<string>? diagnostic = null,
        Func<ServerRecord, CancellationToken, Task<HandshakeResult>>? probe = null, TimeSpan? lockTimeout = null, TimeSpan? readyTimeout = null)
    {
        _executable = Path.GetFullPath(executable);
        _recordPath = Path.GetFullPath(recordPath);
        _diagnostic = diagnostic;
        _probe = probe ?? ((record, cancellation) => RemoteRepositoryClient.ProbeAsync("127.0.0.1", record.Port, record.Token, cancellation));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(60);
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(10);
    }

    internal Task<ServerRecord> StartAsync(Func<ServerRecord, CancellationToken, Task> shutdown, CancellationToken cancellation)
    {
        CancellationTokenSource linked;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
            if (_activeStarts++ == 0) _startsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var token = linked.Token;
        var owned = StartOwnedAsync();
        // A canceled caller leaves the spawned child's readiness/decision-lock
        // responsibility with its owner rather than allowing another startup.
        _ = owned.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return owned.WaitAsync(token);

        async Task<ServerRecord> StartOwnedAsync()
        {
            // Publish ownership first, without depending on the UI pumping work.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try { return await StartCoreAsync(shutdown, token).ConfigureAwait(false); }
            finally
            {
                linked.Dispose();
                lock (_lifecycleGate)
                {
                    if (--_activeStarts == 0)
                    {
                        if (_disposed && _cancellationComplete) ReleaseResources();
                        _startsDrained.TrySetResult();
                    }
                }
            }
        }
    }
    private async Task<ServerRecord> StartCoreAsync(Func<ServerRecord, CancellationToken, Task> shutdown, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        using var decisionLock = await AcquireDecisionLockAsync(cancellation).ConfigureAwait(false);
        for (var discovery = 0; discovery < 16; discovery++)
        {
            cancellation.ThrowIfCancellationRequested();
            var record = await ReadRecordAsync(cancellation).ConfigureAwait(false);
            if (record == null)
            {
                var intents = new LocalStartupIntentFile(_recordPath);
                var intent = await intents.ReadAsync(cancellation).ConfigureAwait(false);
                if (intent != null)
                {
                    _diagnostic?.Invoke("Observing pending local startup: " + _recordPath);
                    await intents.WaitForRecordAsync(intent, ReadRecordAsync, _readyTimeout, cancellation).ConfigureAwait(false);
                    continue;
                }
                // A manual server does not take our decision lock or write our
                // intent. Subscribe before checking its lifetime lock/record.
                using var publication = new RecordPublicationObserver(_recordPath);
                PublicationSubscribed?.Invoke(publication);
                if (await ReadRecordAsync(cancellation).ConfigureAwait(false) != null) continue;
                if (!LifetimeLockIsAvailable())
                {
                    _diagnostic?.Invoke("Observing manually started local server: " + _recordPath);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    deadline.CancelAfter(_readyTimeout);
                    try { await publication.WaitAsync(ReadRecordAsync, null, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
                    { throw new IOException($"Local server lifetime lock is held without a published record after {_readyTimeout.TotalSeconds:g} seconds; no successor started: " + _recordPath, error); }
                    continue;
                }
                return await LaunchAsync(cancellation).ConfigureAwait(false);
            }
            if (IsAlive(record.ProcessId))
            {
                var probe = await ProbeRecordAsync(record, cancellation).ConfigureAwait(false);
                if (probe == null) continue; // Identity changed while probing: rediscover under the same lock.
                if (probe.InstanceId != record.InstanceId)
                {
                    _diagnostic?.Invoke($"Local record/probe instance mismatch for PID {record.ProcessId}, port {record.Port}; rediscovering");
                    continue;
                }
                if (record.Build == Protocol.Build)
                {
                    var intents = new LocalStartupIntentFile(_recordPath);
                    var intent = await intents.ReadAsync(cancellation).ConfigureAwait(false);
                    if (intent is { ChildPid: > 0 } && intent.ChildPid == record.ProcessId)
                    {
                        using var child = LocalStartupIntentFile.ObserveChild(intent);
                        if (child != null) await intents.RemoveAsync(intent).ConfigureAwait(false);
                    }
                    return record;
                }

                // Only the authenticated owner can authorize an upgrade. Incompatible
                // protocols fail probing/handshake and require manual intervention.
                using var upgrade = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                upgrade.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    using var oldProcess = Process.GetProcessById(record.ProcessId);
                    // Open the stable OS reference before requesting shutdown;
                    // a later PID reuse must not become our exit wait target.
                    _ = oldProcess.SafeHandle;
                    await shutdown(record, upgrade.Token).ConfigureAwait(false);
                    await oldProcess.WaitForExitAsync(upgrade.Token).ConfigureAwait(false);
                }
                catch (ArgumentException) when (!IsAlive(record.ProcessId)) { continue; }
                catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
                {
                    throw new IOException($"Timed out after 10 seconds requesting Shutdown or waiting for local server PID {record.ProcessId} to exit; no successor started", error);
                }
                if (IsAlive(record.ProcessId)) throw new IOException($"Shutdown accepted but local server PID {record.ProcessId} has not exited");
                continue;
            }

            // Re-read under the client decision lock before deleting a confirmed dead
            // record. Manual editors do not participate in this locking contract.
            var current = await ReadRecordAsync(cancellation).ConfigureAwait(false);
            if (current != record || IsAlive(record.ProcessId)) continue;
            File.Delete(_recordPath);
        }
        throw new IOException("Local server record kept changing or its probe returned another instance; record preserved. Check the instance record and running server before retrying.");
    }
    private bool LifetimeLockIsAvailable()
    {
        try { using var held = new FileStream(_recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { return false; }
    }

    private async Task<FileStream> AcquireDecisionLockAsync(CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        IOException? last = null;
        var backoff = TimeSpan.FromMilliseconds(250);
        while (elapsed.Elapsed < _lockTimeout)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(_recordPath + ".startup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error)
            {
                if (last == null) _diagnostic?.Invoke("Waiting for local server startup decision lock: " + _recordPath);
                last = error;
            }
            var remaining = _lockTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < backoff ? remaining : backoff, cancellation).ConfigureAwait(false);
            backoff = TimeSpan.FromMilliseconds(Math.Min(1000, backoff.TotalMilliseconds * 2));
        }
        throw new IOException($"Timed out after {_lockTimeout.TotalSeconds:g} seconds waiting for the local server startup decision lock: " + _recordPath, last);
    }

    private async Task<ServerRecord?> ReadRecordAsync(CancellationToken cancellation)
    {
        try
        {
            using var input = new FileStream(_recordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 16 * 1024) throw new InvalidDataException("Oversized local server record; preserved: " + _recordPath);
            var record = await JsonSerializer.DeserializeAsync(input, RemoteJson.Default.ServerRecord, cancellation).ConfigureAwait(false);
            if (record == null || record.ProcessId <= 0 || record.Port is <= 0 or > 65535 ||
                string.IsNullOrEmpty(record.InstanceId) || string.IsNullOrEmpty(record.Token))
                throw new InvalidDataException("Invalid local server instance record; record preserved: " + _recordPath);
            return record;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException error) { throw new InvalidDataException("Malformed local server instance record; record preserved: " + _recordPath, error); }
    }

    private async Task<HandshakeResult?> ProbeRecordAsync(ServerRecord record, CancellationToken cancellation)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            HandshakeResult result;
            try
            {
                result = await _probe(record, cancellation).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or TimeoutException)
            {
                cancellation.ThrowIfCancellationRequested();
                last = error;
                if (!await RecordStillMatchesAsync(record, cancellation).ConfigureAwait(false)) return null;
                if (!IsAlive(record.ProcessId)) return null;
                _diagnostic?.Invoke($"Local probe {attempt + 1}/3 failed for PID {record.ProcessId}, port {record.Port}: {error.GetType().Name}");
                if (attempt < 2) await Task.Delay(1000, cancellation).ConfigureAwait(false);
                continue;
            }
            // Record I/O is discovery, not a transient network probe failure.
            return await RecordStillMatchesAsync(record, cancellation).ConfigureAwait(false) ? result : null;
        }
        // A live PID does not prove identity, and an unavailable endpoint does not
        // prove death. Preserve its record instead of spawning a competing process.
        throw new IOException($"Local server PID {record.ProcessId}, port {record.Port} is alive but all 3 probes failed ({last?.GetType().Name}). Record preserved; check authentication/protocol compatibility or stop the old server manually before retrying.", last);
    }

    private async Task<bool> RecordStillMatchesAsync(ServerRecord expected, CancellationToken cancellation)
    {
        var current = await ReadRecordAsync(cancellation).ConfigureAwait(false);
        if (current == null && IsAlive(expected.ProcessId))
            throw new IOException($"Local record disappeared while PID {expected.ProcessId} was still alive; refusing to start a duplicate server");
        return current == expected;
    }

    private async Task<ServerRecord> LaunchAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : _executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (_executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(_executable);
        start.ArgumentList.Add("--record"); start.ArgumentList.Add(_recordPath);
        _process?.Dispose();
        if (BeforeProcessStarted != null) await BeforeProcessStarted(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var intents = new LocalStartupIntentFile(_recordPath);
        var intent = await intents.BeginAsync(cancellation).ConfigureAwait(false);
        Process process;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new IOException("Cannot start local server");
        }
        catch { await intents.RemoveAsync(intent).ConfigureAwait(false); throw; }
        _process = process;
        process.StandardInput.Close();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputTask = CaptureReadyAsync(process.StandardOutput, ready);
        var errorTask = CaptureTailAsync(process.StandardError);
        _stdout = outputTask; _stderr = errorTask;
        if (BeforeChildBound != null) await BeforeChildBound(process, cancellation).ConfigureAwait(false);
        intent = await intents.BindAsync(intent, process).ConfigureAwait(false);
        var startup = ObserveStartupAsync(process, ready.Task, outputTask, errorTask);
        try
        {
            _diagnostic?.Invoke($"Started local server PID {process.Id}");
            if (AfterProcessStarted != null) await AfterProcessStarted(process, ready.Task, cancellation).ConfigureAwait(false);
        }
        catch
        {
            // Even a callback failure cannot release the decision lock before
            // this owned child has completed its bounded readiness observation.
            try { await startup.ConfigureAwait(false); } catch (IOException) { }
            throw;
        }
        var record = await startup.ConfigureAwait(false);
        await intents.RemoveAsync(intent).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return record;
    }

    private async Task<ServerRecord> ObserveStartupAsync(Process process, Task<string> ready, Task<string> outputTask, Task<string> errorTask)
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(_readyTimeout);
        var exit = process.WaitForExitAsync(deadline.Token);
        try
        {
            await Task.WhenAny(ready, exit).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (exit.IsCompleted || process.HasExited)
            {
                await exit.ConfigureAwait(false);
                var output = await outputTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                var error = await errorTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                throw new IOException($"Local server PID {process.Id} exited before startup (exit {process.ExitCode}). " + error.Trim() + " " + output.Trim());
            }
            var instance = await ready.ConfigureAwait(false);
            if (instance.Length == 0) throw new IOException($"Local server PID {process.Id} closed startup output before its ready notification");
            var record = await ReadRecordAsync(deadline.Token).ConfigureAwait(false);
            if (record == null || record.ProcessId != process.Id || record.InstanceId != instance || process.HasExited)
                throw new IOException($"Local server PID {process.Id} ready notification does not match its live instance record; record preserved");
            return record;
        }
        catch (OperationCanceledException error)
        {
            throw new IOException($"Local server PID {process.Id} did not publish its ready notification within {_readyTimeout.TotalSeconds:g} seconds; check its startup diagnostics", error);
        }
        finally { deadline.Cancel(); }
    }

    private static async Task<string> CaptureReadyAsync(StreamReader reader, TaskCompletionSource<string> ready)
    {
        var tail = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                tail.AppendLine(line);
                if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
                const string prefix = "SOURCEGIT_READY ";
                if (line.StartsWith(prefix, StringComparison.Ordinal)) ready.TrySetResult(line[prefix.Length..].Trim());
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        finally { ready.TrySetResult(""); }
        return tail.ToString();
    }

    private static async Task<string> CaptureTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        return tail.ToString();
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private void ReleaseResources() { _process?.Dispose(); _lifetime.Dispose(); }
    private static TaskCompletionSource CompletedDrain()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task drained;
        lock (_lifecycleGate) drained = _startsDrained.Task;
        await drained.ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _lifetime.Cancel(); }
            finally
            {
                _cancellationComplete = true;
                if (_activeStarts == 0) ReleaseResources();
            }
        }
        // The server completes writes and idles out independently.
    }
}
