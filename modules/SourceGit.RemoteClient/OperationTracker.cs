using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

public sealed record PendingOperation(string? InstanceId, string Method, JsonElement Request, string State = "awaitingRegistration");

/// <summary>Tracks writes across channel replacement without submitting them a second time.</summary>
public sealed class OperationTracker
{
    private readonly ConcurrentDictionary<string, PendingOperation> _pending = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _sizes = new();
    private readonly HashSet<string> _running = new();
    private readonly Dictionary<string, OperationInfo> _resolved = new();
    private long _retainedBytes;
    public int Capacity { get; }
    public long ByteCapacity { get; }
    public IReadOnlyDictionary<string, PendingOperation> Pending => _pending;
    public OperationTracker(int capacity = 1024, long byteCapacity = 16 * 1024 * 1024)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
        Capacity = capacity; ByteCapacity = byteCapacity;
    }

    // Explicit acknowledgement clears local uncertainty; it never cancels or resubmits a server write.
    public bool Forget(string id)
    {
        lock (_gate)
        {
            if (_running.Contains(id)) throw new InvalidOperationException("Cannot forget an operation while its caller is still waiting");
            if (!_pending.TryRemove(id, out _)) return false;
            _retainedBytes -= _sizes[id]; _sizes.Remove(id);
            return true;
        }
    }

    internal void Observe(string? instance, OperationInfo info)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(info.Id, out var pending) || pending.InstanceId != instance) return;
            if (info.State is "succeeded" or "failed")
            {
                if (_running.Contains(info.Id)) _resolved[info.Id] = info;
                else Forget(info.Id);
            }
            else if (OperationState.IsActive(info.State)) _pending[info.Id] = pending with { State = "registered" };
        }
    }

    internal void BindInstance(string? instance)
    {
        lock (_gate)
            foreach (var entry in _pending)
                if (entry.Value.InstanceId != instance) _pending[entry.Key] = entry.Value with { State = "unknownInstance" };
    }

    internal async Task<OperationInfo> RunAsync(RemoteRepositoryClient origin, string id, string method,
        JsonElement request, Func<Task<OperationInfo>> submit, CancellationToken cancellation,
        Action<OperationProgress>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var size = System.Text.Encoding.UTF8.GetByteCount(request.GetRawText());
        lock (_gate)
        {
            if (_pending.ContainsKey(id)) throw new InvalidOperationException("Operation id is still pending; query its original instance instead of submitting it again");
            if (_pending.Count >= Capacity || size > ByteCapacity - _retainedBytes)
                throw new InvalidOperationException("Pending operation capacity reached; resolve or explicitly forget an existing record before submitting a write");
            _pending[id] = new(origin.InstanceId, method, request.Clone());
            _sizes[id] = size; _retainedBytes += size;
            _running.Add(id);
        }
        var current = origin;
        void OnProgress(OperationProgress value) { if (value.OperationId == id) progress?.Invoke(value); }
        current.OperationProgress += OnProgress;
        try
        {
            OperationInfo operation;
            try { operation = await submit().ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // A lost/late acknowledgement does not mean the persistent write
                // failed. Query the original id on this channel; never resubmit it.
                operation = await QueryAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (CanRecover(current, error))
            {
                await RecoverAsync().ConfigureAwait(false);
                operation = await QueryAsync().ConfigureAwait(false);
            }
            while (OperationState.IsActive(operation.State))
            {
                Observe(current.InstanceId, operation);
                operation = await QueryAsync(wait: true).ConfigureAwait(false);
            }
            Observe(current.InstanceId, operation);
            return operation;
        }
        finally
        {
            current.OperationProgress -= OnProgress;
            lock (_gate)
            {
                _running.Remove(id);
                if (_resolved.Remove(id)) Forget(id);
            }
        }

        bool CanRecover(RemoteRepositoryClient client, Exception error) => client.IsDisconnected &&
            origin.RecoveryClient != null && !cancellation.IsCancellationRequested &&
            error is IOException or OperationCanceledException or SocketException or ObjectDisposedException;

        async Task RecoverAsync()
        {
            lock (_gate) if (_resolved.ContainsKey(id)) return;
            var replacement = await origin.RecoveryClient!(cancellation).ConfigureAwait(false);
            BindInstance(replacement.InstanceId);
            current.OperationProgress -= OnProgress;
            current = replacement;
            current.OperationProgress += OnProgress;
        }

        async Task<OperationInfo> QueryAsync(bool wait = false)
        {
            while (true)
            {
                lock (_gate) if (_resolved.TryGetValue(id, out var known)) return known;
                // An operation ID has meaning only in its original server instance.
                if (current.InstanceId != origin.InstanceId) return new(id, "unknown");
                try
                {
                    return wait
                        ? await current.CallAsync("WaitOperation", new IdRequest(id), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, cancellation).ConfigureAwait(false)
                        : await current.GetOperationAsync(id, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (CanRecover(current, error)) { await RecoverAsync().ConfigureAwait(false); }
            }
        }
    }
}
