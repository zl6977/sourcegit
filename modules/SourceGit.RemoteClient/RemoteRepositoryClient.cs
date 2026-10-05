using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

public sealed class RemoteRepositoryClient : IAsyncDisposable
{
    private TcpClient? _client;
    private Stream? _stream;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _orderedReplies = new();
    private readonly ConcurrentDictionary<string, MemoryStream> _chunks = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reader;
    private readonly Channel<Envelope> _events = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private Task? _eventReader;
    private int _queuedEvents;
    private readonly TimeSpan _rpcTimeout;
    private Timer? _heartbeat;
    internal Func<Task>? BeforeRpcCancellationClassification { get; set; }
    internal Func<Task>? BeforeDisconnectCancellation { get; set; }
    public string OwnerId { get; }
    public string RecoveryToken { get; }
    public string? InstanceId { get; private set; }
    public OperationTracker Operations { get; }
    public IReadOnlyDictionary<string, PendingOperation> PendingOperations => Operations.Pending;
    public Func<CancellationToken, Task<RemoteRepositoryClient>>? RecoveryClient { get; set; }
    public bool IsDisconnected { get; private set; }
    private readonly ConcurrentDictionary<string, InteractionRequest> _pendingInteractions = new();
    private readonly object _interactionGate = new();
    private Action<InteractionRequest>? _interactionRequested;
    public event Action<RepositoryEvent>? RepositoryChanged;
    public event Action<InteractionRequest> InteractionRequested
    {
        add
        {
            lock (_interactionGate)
            {
                _interactionRequested += value;
                foreach (var request in _pendingInteractions.Values) { var pending = request; _ = Task.Run(() => value(pending)); }
            }
        }
        remove { lock (_interactionGate) _interactionRequested -= value; }
    }
    public event Action<OperationInfo>? OperationCompleted;
    public event Action<OperationProgress>? OperationProgress;
    public event Action<OperationOutput>? OperationOutput;
    public event Action<Exception>? Disconnected;
    public RemoteRepositoryClient(string? ownerId = null, string? recoveryToken = null, TimeSpan? rpcTimeout = null, OperationTracker? operations = null)
    {
        Operations = operations ?? new();
        _rpcTimeout = rpcTimeout ?? TimeSpan.FromMinutes(10);
        if (_rpcTimeout <= TimeSpan.Zero || _rpcTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(rpcTimeout));
        OwnerId = ownerId ?? Guid.NewGuid().ToString("N");
        RecoveryToken = recoveryToken ?? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }
    public static async Task<HandshakeResult> ProbeAsync(string host, int port, string token, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(1);
        await Protocol.WriteAsync(client.GetStream(), new Envelope("probe", "Probe", Protocol.Json(new HandshakeRequest(token, "", ""), RemoteJson.Default.HandshakeRequest)), gate, deadline.Token).ConfigureAwait(false);
        var response = await Protocol.ReadAsync(client.GetStream(), deadline.Token) .ConfigureAwait(false)?? throw new IOException("Probe connection closed");
        if (response.Error != null) throw new IOException(response.Error);
        return response.Data.Deserialize(RemoteJson.Default.HandshakeResult) ?? throw new IOException("Invalid probe response");
    }
    public async Task<HandshakeResult> ConnectAsync(string host, int port, string token, CancellationToken cancellation = default)
    {
        if (_client != null) throw new InvalidOperationException("Client already connected");
        _client = new TcpClient { NoDelay = true };
        // A black-holed SYN (WSL 2 lazy localhost forwarding right after a new listener, VPN, firewall) would otherwise block for
        // the full OS retransmit timeout (~21 s) per attempt; fail each attempt fast so the caller's retry loop can keep going.
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connect.CancelAfter(TimeSpan.FromSeconds(5));
        try { await _client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { _client.Dispose(); _client = null; throw new SocketException(10060); }
        _stream = _client.GetStream();
        // The protocol loops must run on the thread pool, never on the caller's synchronization context: a UI-thread caller
        // that sync-waits on an RPC (RemoteWorkspace.List/Stat use GetAwaiter().GetResult()) would otherwise deadlock, because
        // the loop's continuation delivering the response would be posted to the blocked UI dispatcher.
        _reader = Task.Run(ReadLoop);
        _eventReader = Task.Run(ReadEvents);
        var result = await CallAsync("Handshake", new HandshakeRequest(token, OwnerId, RecoveryToken), RemoteJson.Default.HandshakeRequest, RemoteJson.Default.HandshakeResult, cancellation).ConfigureAwait(false);
        InstanceId = result.InstanceId;
        Operations.BindInstance(InstanceId);
        _heartbeat = new Timer(async _ => { try { await CallAsync("Ping", "", RemoteJson.Default.String, RemoteJson.Default.String, _lifetime.Token).ConfigureAwait(false); } catch { } }, null, 10000, 10000);
        return result;
    }
    public async Task<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request, JsonTypeInfo<TRequest> requestJson, JsonTypeInfo<TResponse> responseJson, CancellationToken cancellation = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (method is "ExecuteGit" or "ExecuteCustomCommand" or "GetOperation" or "WaitOperation") _orderedReplies[id] = 0;
        using var deadline = method == "WaitOperation" ? new CancellationTokenSource() : new CancellationTokenSource(_rpcTimeout);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token, deadline.Token);
        try
        {
            using var cancel = timeout.Token.Register(() => { completion.TrySetCanceled(timeout.Token); _ = CancelRequestAsync(id); });
            await Protocol.WriteAsync(_stream ?? throw new IOException("Not connected"), new Envelope(id, method, Protocol.Json(request, requestJson)), _write, timeout.Token).ConfigureAwait(false);
            var result = await completion.Task.ConfigureAwait(false);
            return result.Deserialize(responseJson) ?? throw new InvalidDataException("Empty response");
        }
        catch (OperationCanceledException ex)
        {
            if (BeforeRpcCancellationClassification != null) await BeforeRpcCancellationClassification().ConfigureAwait(false);
            if (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                throw new TimeoutException($"RPC {method} timed out after {_rpcTimeout.TotalSeconds:g} seconds", ex);
            throw;
        }
        finally { _pending.TryRemove(id, out _); _orderedReplies.TryRemove(id, out _); if (_chunks.TryRemove(id, out var chunk)) chunk.Dispose(); }
    }
    private async Task CancelRequestAsync(string id)
    { try { if (_stream != null) await Protocol.WriteAsync(_stream, new Envelope("", "CancelRequest", Protocol.Json(id, RemoteJson.Default.String)), _write, _lifetime.Token).ConfigureAwait(false); } catch { } }
    private async Task ReadLoop()
    {
        try
        {
            while (await Protocol.ReadAsync(_stream!, _lifetime.Token) is { } message)
            {
                if (message.Event)
                {
                    // Operation events preserve receive order. Modal interactions have their own lane.
                    if (message.Method is "Interaction" or "RepositoryChanged") _ = Task.Run(() => DispatchEvent(message));
                    else
                    {
                        EnqueueEvent(message);
                    }
                    continue;
                }
                if (message.Error != null)
                {
                    if (string.IsNullOrEmpty(message.Id)) throw new IOException(message.Error);
                    if (_pending.TryGetValue(message.Id, out var failed)) failed.TrySetException(new IOException(message.Error));
                    continue;
                }
                if (!_pending.TryGetValue(message.Id, out var pending)) continue;
                if (message.Method == "Chunk")
                {
                    var buffer = _chunks.GetOrAdd(message.Id, _ => new MemoryStream());
                    if (buffer.Length > 256L * 1024 * 1024) throw new InvalidDataException("Response exceeds content limit");
                    buffer.Write(Convert.FromBase64String(message.Data.GetString()!)); continue;
                }
                if (message.Method == "ChunkEnd")
                {
                    if (!_chunks.TryRemove(message.Id, out var buffer)) throw new InvalidDataException("Missing response chunks");
                    using (buffer) using (var document = JsonDocument.Parse(buffer.ToArray())) CompleteResult(message.Id, document.RootElement.Clone(), pending);
                }
                else CompleteResult(message.Id, message.Data, pending);
            }
            throw new EndOfStreamException("Server connection closed");
        }
        catch (Exception ex)
        {
            IsDisconnected = true;
            if (BeforeDisconnectCancellation != null) await BeforeDisconnectCancellation().ConfigureAwait(false);
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
            _heartbeat?.Dispose();
            _lifetime.Cancel();
            _client?.Dispose();
            Disconnected?.Invoke(ex);
        }
        finally { _events.Writer.TryComplete(); }
        }
    private void CompleteResult(string id, JsonElement result, TaskCompletionSource<JsonElement> completion)
    {
        if (!_orderedReplies.ContainsKey(id)) { completion.TrySetResult(result); return; }
        EnqueueEvent(new Envelope(id, "ReplyBarrier", result));
    }
    private void EnqueueEvent(Envelope message)
    {
        if (Interlocked.Increment(ref _queuedEvents) > 4096)
        {
            Interlocked.Decrement(ref _queuedEvents);
            throw new IOException("Event consumer is too slow; reconnect and resynchronize");
        }
        if (!_events.Writer.TryWrite(message))
        {
            Interlocked.Decrement(ref _queuedEvents);
            throw new IOException("Event delivery queue closed; reconnect and resynchronize");
        }
    }
    private async Task ReadEvents()
    {
        await foreach (var message in _events.Reader.ReadAllAsync(_lifetime.Token))
        {
            Interlocked.Decrement(ref _queuedEvents);
            try
            {
                if (message.Method == "ReplyBarrier") { if (_pending.TryGetValue(message.Id, out var completion)) completion.TrySetResult(message.Data); }
                else DispatchEvent(message);
            }
            catch { /* A consumer callback must not break protocol delivery. */ }
        }
    }
    private void DispatchEvent(Envelope message)
    {
        switch (message.Method)
        {
            case "RepositoryChanged": RepositoryChanged?.Invoke(message.Data.Deserialize(RemoteJson.Default.RepositoryEvent)!); break;
            case "Interaction":
                var request = message.Data.Deserialize(RemoteJson.Default.InteractionRequest)!;
                lock (_interactionGate) { if (_pendingInteractions.TryAdd(request.Id, request)) _interactionRequested?.Invoke(request); }
                break;
            case "OperationCompleted":
                var completed = message.Data.Deserialize(RemoteJson.Default.OperationInfo)!;
                Operations.Observe(InstanceId, completed);
                OperationCompleted?.Invoke(completed); break;
            case "OperationProgress": OperationProgress?.Invoke(message.Data.Deserialize(RemoteJson.Default.OperationProgress)!); break;
            case "OperationOutput": OperationOutput?.Invoke(message.Data.Deserialize(RemoteJson.Default.OperationOutput)!); break;
        }
    }
    public Task<RepositoryInfo> OpenRepositoryAsync(string path, CancellationToken cancellation = default) => CallAsync("OpenRepository", new OpenRequest(path), RemoteJson.Default.OpenRequest, RemoteJson.Default.RepositoryInfo, cancellation);
    public Task<string> CloseRepositoryAsync(string leaseId, CancellationToken cancellation = default) => CallAsync("CloseRepository", new IdRequest(leaseId), RemoteJson.Default.IdRequest, RemoteJson.Default.String, cancellation);
    public Task<StatusSnapshot> GetStatusAsync(string repositoryId, bool noOptionalLocks = false, CancellationToken cancellation = default) => CallAsync("GetStatus", new QueryRequest(repositoryId, NoOptionalLocks: noOptionalLocks), RemoteJson.Default.QueryRequest, RemoteJson.Default.StatusSnapshot, cancellation);
    public Task<RefsSnapshot> GetRefsAsync(string repositoryId, CancellationToken cancellation = default) => CallAsync("GetRefs", new QueryRequest(repositoryId), RemoteJson.Default.QueryRequest, RemoteJson.Default.RefsSnapshot, cancellation);
    public Task<HistoryPage> GetHistoryAsync(QueryRequest request, CancellationToken cancellation = default) => CallAsync("GetHistory", request, RemoteJson.Default.QueryRequest, RemoteJson.Default.HistoryPage, cancellation);
    public Task<CommitInfo> GetCommitAsync(string repositoryId, string revision, CancellationToken cancellation = default) => CallAsync("GetCommit", new QueryRequest(repositoryId, revision), RemoteJson.Default.QueryRequest, RemoteJson.Default.CommitInfo, cancellation);
    public Task<BlameData> GetBlameAsync(string repositoryId, string revision, string file, bool ignoreWhitespace = false, CancellationToken cancellation = default) => CallAsync("GetBlame", new QueryRequest(repositoryId, revision, file, 0, 100, ignoreWhitespace), RemoteJson.Default.QueryRequest, RemoteJson.Default.BlameData, cancellation);
    public Task<DiffInfo> GetDiffAsync(QueryRequest request, CancellationToken cancellation = default) => CallAsync("GetDiff", request, RemoteJson.Default.QueryRequest, RemoteJson.Default.DiffInfo, cancellation);
    public async Task<FileResult> FileAsync(FileRequest request, CancellationToken cancellation = default)
    {
        if (request.Action is "stat" or "exists" or "read" or "list" or "hash")
            return await CallAsync("File", request, RemoteJson.Default.FileRequest, RemoteJson.Default.FileResult, cancellation).ConfigureAwait(false);
        request = request with { OperationId = request.OperationId ?? Guid.NewGuid().ToString("N") };
        var operation = await Operations.RunAsync(this, request.OperationId, "File", Protocol.Json(request, RemoteJson.Default.FileRequest),
            async () => new OperationInfo(request.OperationId, "succeeded", FileResult:
                await CallAsync("File", request, RemoteJson.Default.FileRequest, RemoteJson.Default.FileResult, cancellation).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
        return operation.FileResult ?? throw new IOException(operation.Error ?? "File operation outcome unknown; refresh before retrying");
    }
    public Task<FileResult> ListDirectoryAsync(string path, int offset = 0, int count = 200, CancellationToken cancellation = default, string? cursor = null) => CallAsync("ListDirectory", new DirectoryRequest(path, offset, count, cursor), RemoteJson.Default.DirectoryRequest, RemoteJson.Default.FileResult, cancellation);
    public Task<string> ReleaseDirectoryAsync(string cursor, CancellationToken cancellation = default) => CallAsync("ReleaseDirectory", new IdRequest(cursor), RemoteJson.Default.IdRequest, RemoteJson.Default.String, cancellation);
    public async IAsyncEnumerable<FileEntry[]> DirectoryPagesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation = default)
    {
        string? cursor = null;
        var offset = 0;
        try
        {
            do
            {
                var page = await ListDirectoryAsync(path, offset, cancellation: cancellation, cursor: cursor).ConfigureAwait(false);
                cursor = page.DirectoryCursor; offset = page.NextOffset;
                cancellation.ThrowIfCancellationRequested();
                yield return page.Entries ?? [];
            } while (cursor != null);
        }
        finally
        {
            if (cursor != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ReleaseDirectoryAsync(cursor, cleanup.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
            }
        }
    }
    public Task<EnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellation = default) => CallAsync("GetEnvironment", "", RemoteJson.Default.String, RemoteJson.Default.EnvironmentInfo, cancellation);
    public async Task<OperationInfo> GetOperationAsync(string id, CancellationToken cancellation = default)
    {
        var info = await CallAsync("GetOperation", new IdRequest(id), RemoteJson.Default.IdRequest, RemoteJson.Default.OperationInfo, cancellation).ConfigureAwait(false);
        Operations.Observe(InstanceId, info);
        return info;
    }
    public Task<string> ConfigureAutoFetchAsync(string repositoryId, int intervalMinutes, bool prune = true, CancellationToken cancellation = default) => CallAsync("ConfigureAutoFetch", new AutoFetchRequest(repositoryId, intervalMinutes, prune), RemoteJson.Default.AutoFetchRequest, RemoteJson.Default.String, cancellation);
    public Task<string> ShutdownAsync(CancellationToken cancellation = default) => CallAsync("Shutdown", "", RemoteJson.Default.String, RemoteJson.Default.String, cancellation);
    public async Task<string> SubmitInteractionAsync(InteractionReply reply, CancellationToken cancellation = default)
    {
        var response = await CallAsync("SubmitInteraction", reply, RemoteJson.Default.InteractionReply, RemoteJson.Default.String, cancellation).ConfigureAwait(false);
        _pendingInteractions.TryRemove(reply.Id, out _); return response;
    }
    public async Task<GitResult> ExecuteGitAsync(GitRequest request, CancellationToken cancellation = default, Action<OperationProgress>? progress = null)
    {
        if (request.ReadOnly) return await CallAsync("ExecuteGit", request, RemoteJson.Default.GitRequest, RemoteJson.Default.GitResult, cancellation).ConfigureAwait(false);
        request = request with { OperationId = request.OperationId ?? Guid.NewGuid().ToString("N") };
        var operation = await Operations.RunAsync(this, request.OperationId, "ExecuteGit", Protocol.Json(request, RemoteJson.Default.GitRequest),
            () => CallAsync("ExecuteGit", request, RemoteJson.Default.GitRequest, RemoteJson.Default.OperationInfo, cancellation), cancellation, progress).ConfigureAwait(false);
        return operation.Result ?? throw new IOException(operation.Error ?? "Operation outcome unknown; refresh repository before retrying");
    }
    public async Task<GitResult> ExecuteCustomAsync(CustomCommandRequest request, CancellationToken cancellation = default, Action<OperationProgress>? progress = null)
    {
        var operation = await Operations.RunAsync(this, request.OperationId, "ExecuteCustomCommand", Protocol.Json(request, RemoteJson.Default.CustomCommandRequest),
            () => CallAsync("ExecuteCustomCommand", request, RemoteJson.Default.CustomCommandRequest, RemoteJson.Default.OperationInfo, cancellation), cancellation, progress).ConfigureAwait(false);
        return operation.Result ?? throw new IOException(operation.Error ?? "Custom command outcome unknown; refresh before retrying");
    }
    public async Task<byte[]> ReadFileAsync(string repositoryId, string path, CancellationToken cancellation = default)
    {
        using var output = new MemoryStream();
        long offset = 0;
        do
        {
            var result = await FileAsync(new FileRequest(repositoryId, "read", path, Offset: offset), cancellation).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(result.ContentBase64!); output.Write(bytes); offset += bytes.Length;
            if (offset >= result.Length || bytes.Length == 0) return output.ToArray();
        } while (output.Length < 256L * 1024 * 1024);
        throw new IOException("Content exceeds 256 MiB client limit");
    }
    private int _disposed;
    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Publish channel loss before canceling pending long waits. Their
        // continuations must be able to recover a persistent write immediately.
        IsDisconnected = true;
        _heartbeat?.Dispose(); _lifetime.Cancel(); _client?.Dispose();
        if (_reader != null) { try { await _reader.ConfigureAwait(false); } catch { } }
        // Consumer callbacks run outside the protocol reader. A stuck callback must
        // not keep a channel or upgrade alive; cancellation stops further delivery.
        if (_eventReader != null) { try { await _eventReader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { } }
        _lifetime.Dispose(); _write.Dispose();
    }
}
