using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.Core;
using SourceGit.Server;
using Operation = SourceGit.Server.OperationStore.Operation;

if (args.Length > 0 && args[0] == "helper") { Environment.Exit(await Helpers.RunAsync(args.Skip(1).ToArray())); return; }
if (args.Length > 0 && args[0] == "bootstrap") { Environment.Exit(await Bootstrap.RunAsync(args.Skip(1).ToArray())); return; }
var recordPath = GetOption("--record") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sourcegit-server", "instance-" + Protocol.Build + ".json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(recordPath))!);
using var server = new RemoteServer(recordPath, GetOption("--ready-socket"), GetOption("--ready-nonce"));
await server.RunAsync();
string? GetOption(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

sealed class RemoteServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly RepositoryService _repositories = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _recordPath;
    private readonly string? _readySocket, _readyNonce;
    private readonly OperationStore _operations = new();
    private readonly DirectorySnapshots _directorySnapshots = new();
    private readonly TimeSpan _operationTimeout = ExecutionTimeout();
    private static TimeSpan ExecutionTimeout()
    {
        var configured = Environment.GetEnvironmentVariable("SOURCEGIT_OPERATION_TIMEOUT_SECONDS");
        if (configured == null) return TimeSpan.FromHours(1);
        if (!double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 4294967)
            throw new ArgumentException("SOURCEGIT_OPERATION_TIMEOUT_SECONDS must be positive and at most 4294967");
        return TimeSpan.FromSeconds(seconds);
    }
    private readonly ConcurrentDictionary<string, PendingInteraction> _interactions = new();
    private readonly ConcurrentDictionary<string, Timer> _fetchTimers = new(StringComparer.Ordinal);
    private int _operationWaiters;
    internal int ActiveOperationWaiters => Volatile.Read(ref _operationWaiters);
    internal const int OperationWaitersPerChannel = 64;
    internal const int OperationWaitersGlobal = 1024;
    private readonly object _ownerLock = new();
    private string? _owner, _recovery;
    private Connection? _connection;
    private readonly ConcurrentDictionary<Connection, byte> _channels = new();
    private readonly ServerDeadlines<Connection> _deadlines;
    private readonly TimeSpan _grace = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_GRACE_SECONDS"), out var seconds) ? Math.Max(1, seconds) : 300);
    private readonly TimeSpan _channelGrace = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_CHANNEL_GRACE_SECONDS"), out var channelSeconds) ? Math.Max(1, channelSeconds) : 60);
    private static readonly bool DebugLog = Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1";
    private static string Truncate(string text) => text.Length > 400 ? text[..400] : text;
    private string _helperPath = "";
    internal Func<string, CancellationToken, Task>? BeforeReply { get; set; }
    internal Func<Task>? BeforeRecordPublished { get; set; }
    internal Action? AfterChannelClosed { get; set; }
    internal Action<string, Exception>? RequestFailed { get; set; }
    private readonly TaskCompletionSource<ServerRecord> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<ServerRecord> Ready => _ready.Task;
    private sealed record PendingInteraction(InteractionRequest Request, TaskCompletionSource<InteractionReply> Completion);
    private sealed class Connection
    {
        public required TcpClient Client;
        public required NetworkStream Stream;
        public string OwnerId = "";
        public readonly string ChannelId = Guid.NewGuid().ToString("N");
        public readonly SemaphoreSlim WriteGate = new(1);
        public readonly ConcurrentDictionary<string, CancellationTokenSource> Requests = new();
        public readonly SemaphoreSlim RequestsGate = new(16);
        public int OperationWaiters;
    }
    public RemoteServer(string recordPath, string? readySocket = null, string? readyNonce = null)
    {
        _recordPath = Path.GetFullPath(recordPath);
        _readySocket = readySocket; _readyNonce = readyNonce;
        _deadlines = new(_ownerLock, _grace, _channelGrace, () => _operations.HasActive, () =>
        {
            if (_connection != null || !_operations.TryBeginIdleShutdown()) return false;
            _listener.Stop(); return true;
        });
        _repositories.Changed += OnChanged;
        _repositories.Progress += progress => _ = SendEvent("OperationProgress", Protocol.Json(progress, RemoteJson.Default.OperationProgress));
        _repositories.Output = output => SendEvent("OperationOutput", Protocol.Json(output, RemoteJson.Default.OperationOutput), cancellation: _operations.TryGetValue(Key(output.OperationId), out var operation) ? operation.ExecutionToken : default);
    }
    public async Task RunAsync()
    {
        // An OS file lock makes stale records harmless and protects against concurrent starts.
        using var instanceLock = new FileStream(_recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (BeforeRecordPublished != null) await BeforeRecordPublished();
        _listener.Start(32);
        var record = new ServerRecord(((IPEndPoint)_listener.LocalEndpoint).Port, _token, _instance, Environment.ProcessId);
        var temporary = _recordPath + ".tmp-" + _instance;
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, _recordPath, true);
        _helperPath = await Helpers.InstallAsync(Path.GetDirectoryName(_recordPath)!);
        _repositories.PrepareProcess = (start, operation) =>
        {
            start.Environment["SOURCEGIT_HELPER_PORT"] = record.Port.ToString();
            start.Environment["SOURCEGIT_HELPER_TOKEN"] = operation != null && _operations.TryGetValue(Key(operation), out var active) ? active.HelperToken : "";
            start.Environment["SOURCEGIT_OPERATION_ID"] = operation ?? "query";
            start.Environment["GIT_ASKPASS"] = _helperPath;
            start.Environment["SSH_ASKPASS"] = _helperPath;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            var editorCommand = "'" + _helperPath.Replace('\\', '/').Replace("'", "'\"'\"'") + "'";
            start.Environment["GIT_EDITOR"] = editorCommand;
            start.Environment["GIT_SEQUENCE_EDITOR"] = editorCommand;
            return Task.CompletedTask;
        };
        // The GUI may crash after spawn; a closed startup pipe must not kill
        // the independently running server after it publishes its record.
        try { Console.WriteLine("SOURCEGIT_READY " + _instance); }
        catch (IOException) { }
        await Bootstrap.NotifyReadyAsync(_readySocket, _readyNonce, record, _recordPath);
        _ready.TrySetResult(record);
        // A live client pings every 10 s; a channel silent for 60 s belongs to a dead peer (crash, broken NAT
        // forwarding that never delivered FIN/RST) and must be aborted so its ownership cannot pin the instance.
        _deadlines.Start();
        try { while (true) { var client = await _listener.AcceptTcpClientAsync(); _ = HandleAsync(client); } }
        catch (SocketException) { }
        finally
        {
            _deadlines.Dispose();
            // The lifetime lock is still held here. Ownership is also checked so
            // a future startup refactor or an external edit cannot erase another record.
            try
            {
                if (File.Exists(_recordPath))
                {
                    var current = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_recordPath), RemoteJson.Default.ServerRecord);
                    if (current?.InstanceId == _instance && current.ProcessId == Environment.ProcessId)
                        File.Delete(_recordPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine("SOURCEGIT_RECORD_CLEANUP " + error.GetType().Name + ": record preserved");
            }
        }
    }
    private async Task HandleAsync(TcpClient client)
    {
        var connection = new Connection { Client = client, Stream = client.GetStream() };
        var ready = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = await Protocol.ReadAsync(connection.Stream, timeout.Token) ?? throw new IOException("Handshake required");
            if (first.Method == "Helper") { await HandleHelperAsync(connection, first); return; }
            if (first.Method is not ("Handshake" or "Probe")) throw new UnauthorizedAccessException("Handshake required");
            var handshake = first.Data.Deserialize(RemoteJson.Default.HandshakeRequest) ?? throw new InvalidDataException();
            if (!SecureEquals(handshake.Token, _token)) throw new UnauthorizedAccessException("Authentication failed");
            if (handshake.Version != Protocol.Version || handshake.Build != Protocol.Build) throw new InvalidOperationException("Protocol/build mismatch");
            if (first.Method == "Probe")
            {
                await Reply(connection, first.Id, Protocol.Json(new HandshakeResult(_instance, Protocol.Version, Protocol.Build, ["git", "files", "watcher", "operations", "interaction", "chunks"]), RemoteJson.Default.HandshakeResult));
                return;
            }
            lock (_ownerLock)
            {
                if (_owner != null && (_owner != handshake.OwnerId || !SecureEquals(_recovery!, handshake.RecoveryToken)))
                {
                    // Takeover: a different owner is accepted as soon as the previous owner has no live channel at all
                    // (crash or exit) and no operation is in flight; a live connection is never displaced.
                    if (_connection != null) throw new InvalidOperationException("Instance owned by another GUI");
                    if (_operations.HasActive) throw new InvalidOperationException("Server has active operations; wait for them to finish");
                    _owner = null;
                    foreach (var fetchTimer in _fetchTimers.Values) fetchTimer.Dispose();
                    _fetchTimers.Clear(); _operations.Clear(); _repositories.CloseAll();
                }
                // One GUI owner may use several aliases/channels for the same host; another owner is still rejected.
                _owner = handshake.OwnerId; _recovery = handshake.RecoveryToken; _connection = connection;
                connection.OwnerId = handshake.OwnerId;
                _channels[connection] = 0;
                _deadlines.Add(connection, client.Close);
                if (DebugLog) Console.Error.WriteLine($"CHANNEL + owner={handshake.OwnerId} channels={_channels.Count}");
            }
            await Reply(connection, first.Id, Protocol.Json(new HandshakeResult(_instance, Protocol.Version, Protocol.Build, ["git", "files", "watcher", "operations", "interaction", "chunks"]), RemoteJson.Default.HandshakeResult));
            foreach (var pending in _interactions.Values) await Protocol.WriteAsync(connection.Stream, new Envelope("", "Interaction", Protocol.Json(pending.Request, RemoteJson.Default.InteractionRequest), Event: true), connection.WriteGate);
            ready = true;
            while (await Protocol.ReadAsync(connection.Stream) is { } request)
            {
                _deadlines.Touch(connection);
                if (request.Method == "CancelRequest")
                {
                    if (connection.Requests.TryGetValue(request.Data.GetString()!, out var cts))
                    {
                        // Completion may remove and dispose it after this lookup.
                        try { cts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    continue;
                }
                if (connection.Requests.Count > 128 + Volatile.Read(ref connection.OperationWaiters) && request.Method is not ("Ping" or "SubmitInteraction" or "GetOperation" or "WaitOperation"))
                { await Protocol.WriteAsync(connection.Stream, new Envelope(request.Id, "Result", Protocol.Null, "Too many pending requests"), connection.WriteGate); continue; }
                _ = ProcessAsync(connection, request);
            }
        }
        catch (Exception ex)
        { if (!ready) Console.Error.WriteLine("SOURCEGIT_REJECTED " + ex.GetType().Name + ": " + ex.Message); try { await Protocol.WriteAsync(connection.Stream, new Envelope("", "Error", Protocol.Null, ex.Message), connection.WriteGate); } catch { } }
        finally
        {
            lock (_ownerLock)
            {
                var wasOwnerChannel = _channels.TryRemove(connection, out _);
                if (_connection == connection) _connection = _channels.Keys.FirstOrDefault();
                if (wasOwnerChannel) _deadlines.Remove(connection);
                if (DebugLog) Console.Error.WriteLine($"CHANNEL - wasOwner={wasOwnerChannel} channels={_channels.Count} owner={_owner}");
            }
            _directorySnapshots.ReleaseOwner(connection.ChannelId);
            foreach (var request in connection.Requests.Values) { try { request.Cancel(); } catch (ObjectDisposedException) { } }
            client.Dispose();
            AfterChannelClosed?.Invoke();
        }
    }
    private async Task ProcessAsync(Connection connection, Envelope request)
    {
        var stopwatch = DebugLog ? System.Diagnostics.Stopwatch.StartNew() : null;
        using var cancellation = new CancellationTokenSource();
        connection.Requests[request.Id] = cancellation;
        var needsGate = request.Method is not ("Ping" or "SubmitInteraction" or "GetOperation" or "WaitOperation" or "ReleaseDirectory");
        var enteredGate = false;
        var enteredOperationWait = false;
        var shutdownAccepted = false;
        try
        {
            if (needsGate) { await connection.RequestsGate.WaitAsync(cancellation.Token); enteredGate = true; }
            if (request.Method == "WaitOperation")
            {
                var channelCount = Interlocked.Increment(ref connection.OperationWaiters);
                var globalCount = Interlocked.Increment(ref _operationWaiters);
                enteredOperationWait = true;
                if (channelCount > OperationWaitersPerChannel || globalCount > OperationWaitersGlobal)
                    throw new IOException("Operation wait capacity reached; cancel an existing wait before retrying");
            }
            JsonElement result;
            switch (request.Method)
            {
                case "Ping": result = Protocol.Json(_instance, RemoteJson.Default.String); break;
                case "GetStatus": result = Protocol.Json(await new RepositoryQueries(_repositories).StatusAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.StatusSnapshot); break;
                case "GetRefs": result = Protocol.Json(await new RepositoryQueries(_repositories).RefsAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.RefsSnapshot); break;
                case "GetHistory": result = Protocol.Json(await new RepositoryQueries(_repositories).HistoryAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.HistoryPage); break;
                case "GetCommit": result = Protocol.Json(await new RepositoryQueries(_repositories).CommitAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.CommitInfo); break;
                case "GetBlame": result = Protocol.Json(await new RepositoryQueries(_repositories).BlameAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.BlameData); break;
                case "GetDiff": result = Protocol.Json(await new RepositoryQueries(_repositories).DiffAsync(request.Data.Deserialize(RemoteJson.Default.QueryRequest)!, cancellation.Token), RemoteJson.Default.DiffInfo); break;
                case "Shutdown":
                    _operations.BeginShutdown();
                    shutdownAccepted = true;
                    foreach (var timer in _fetchTimers.Values) timer.Dispose();
                    _fetchTimers.Clear();
                    result = Protocol.Json("closing", RemoteJson.Default.String);
                    break;
                case "OpenRepository":
                    var opened = await _repositories.OpenAsync(request.Data.Deserialize(RemoteJson.Default.OpenRequest)!.Path, connection.OwnerId);
                    lock (_ownerLock)
                    {
                        if (_owner != connection.OwnerId)
                        {
                            _repositories.Close(opened.LeaseId, connection.OwnerId);
                            throw new IOException("Repository open belongs to a replaced owner");
                        }
                    }
                    result = Protocol.Json(opened, RemoteJson.Default.RepositoryInfo); break;
                case "CloseRepository":
                    var closeId = request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id;
                    // Protocol v2 IdRequest.Id identifies a lease, never the shared repository.
                    var closed = _repositories.Close(closeId, connection.OwnerId);
                    if (closed != null && _repositories.FindByCommonDirectory(closed.CommonDirectory) == null && _fetchTimers.TryRemove(closed.CommonDirectory, out var closingTimer)) closingTimer.Dispose();
                    result = Protocol.Json("closed", RemoteJson.Default.String); break;
                case "ConfigureAutoFetch":
                    var schedule = request.Data.Deserialize(RemoteJson.Default.AutoFetchRequest)!;
                    var scheduledRepo = _repositories.Get(schedule.RepositoryId);
                    if (_fetchTimers.TryRemove(scheduledRepo.CommonDirectory, out var previous)) previous.Dispose();
                    if (schedule.IntervalMinutes > 0)
                    {
                        var interval = TimeSpan.FromMinutes(Math.Clamp(schedule.IntervalMinutes, 1, 1440));
                        var active = 0;
                        _fetchTimers[scheduledRepo.CommonDirectory] = new Timer(async _ =>
                        {
                            if (_connection == null || Interlocked.CompareExchange(ref active, 1, 0) != 0) return;
                            try
                            {
                                var current = _repositories.FindByCommonDirectory(scheduledRepo.CommonDirectory);
                                if (current == null) return;
                                var remotes = await _repositories.ExecuteAsync(new GitRequest(current.Id, ["remote"]));
                                foreach (var remote in Encoding.UTF8.GetString(Convert.FromBase64String(remotes.OutputBase64)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                {
                                    var disabled = await _repositories.ExecuteAsync(new GitRequest(current.Id, ["config", "--get", $"remote.{remote}.disableautofetch"]));
                                    if (Encoding.UTF8.GetString(Convert.FromBase64String(disabled.OutputBase64)).Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                                    var key = await _repositories.ExecuteAsync(new GitRequest(current.Id, ["config", "--get", $"remote.{remote}.sshkey"]));
                                    var sshKey = key.ExitCode == 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(key.OutputBase64)).Trim() : null;
                                    var id = "autofetch-" + Guid.NewGuid().ToString("N");
                                    var query = new GitRequest(current.Id, schedule.Prune ? ["fetch", "--prune", remote] : ["fetch", remote], false, id, Editor: "None", SSHKey: sshKey);
                                    var operation = Register(connection.OwnerId, id, "autofetch", out var registered);
                                    await RunOperation(operation, query);
                                }
                            }
                            catch (Exception error) { Console.Error.WriteLine("Auto-fetch: " + error.Message); }
                            finally { Volatile.Write(ref active, 0); }
                        }, null, interval, interval);
                    }
                    result = Protocol.Json("scheduled", RemoteJson.Default.String); break;
                case "GetEnvironment":
                    var git = await RepositoryService.RunAsync(RepositoryService.GitStart(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["--version"]), null, cancellation.Token);
                    var capabilities = new List<string> { "git", "files", "watcher", "interaction" };
                    var flow = await RepositoryService.RunAsync(RepositoryService.GitStart(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ["flow", "version"]), null, cancellation.Token);
                    if (flow.ExitCode == 0)
                        capabilities.Add(Encoding.UTF8.GetString(Convert.FromBase64String(flow.OutputBase64)).Contains("next", StringComparison.OrdinalIgnoreCase) ? "git-flow-next" : "git-flow");
                    result = Protocol.Json(new EnvironmentInfo(Environment.OSVersion.ToString(), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Environment.UserName, Encoding.UTF8.GetString(Convert.FromBase64String(git.OutputBase64)).Trim(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), capabilities.ToArray()), RemoteJson.Default.EnvironmentInfo); break;
                case "ListDirectory":
                    var directory = request.Data.Deserialize(RemoteJson.Default.DirectoryRequest)!;
                    result = Protocol.Json(await _directorySnapshots.ReadAsync(connection.ChannelId, directory, cancellation.Token), RemoteJson.Default.FileResult); break;
                case "ReleaseDirectory":
                    _directorySnapshots.Release(connection.ChannelId, request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id);
                    result = Protocol.Json("released", RemoteJson.Default.String); break;
                case "File":
                    var fileRequest = request.Data.Deserialize(RemoteJson.Default.FileRequest)!;
                    if (fileRequest.Action is "stat" or "exists" or "read" or "list" or "hash") result = Protocol.Json(await _repositories.FileAsync(fileRequest), RemoteJson.Default.FileResult);
                    else
                    {
                        if (string.IsNullOrWhiteSpace(fileRequest.OperationId)) throw new ArgumentException("File writes require client-generated operation ID");
                        var fileFingerprint = "file:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fileRequest, RemoteJson.Default.FileRequest)));
                        var fileOperation = Register(connection.OwnerId, fileRequest.OperationId, fileFingerprint, out var fileFresh);
                        if (fileFresh) _ = RunFile(fileOperation, fileRequest);
                        var fileInfo = await fileOperation.Completion.WaitAsync(cancellation.Token);
                        result = Protocol.Json(fileInfo.FileResult ?? throw new IOException(fileInfo.Error ?? "File operation outcome unknown"), RemoteJson.Default.FileResult);
                    }
                    break;
                case "ExecuteGit":
                    var gitRequest = request.Data.Deserialize(RemoteJson.Default.GitRequest)!;
                    if (gitRequest.ReadOnly) result = Protocol.Json(await _repositories.ExecuteAsync(gitRequest, cancellation.Token), RemoteJson.Default.GitResult);
                    else
                    {
                        if (string.IsNullOrWhiteSpace(gitRequest.OperationId)) throw new ArgumentException("Client-generated operation ID required");
                        GitCommandPolicy.Validate(gitRequest.Arguments, false);
                        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(gitRequest, RemoteJson.Default.GitRequest)));
                        var operation = Register(connection.OwnerId, gitRequest.OperationId, fingerprint, out var fresh);
                        if (fresh) _ = RunOperation(operation, gitRequest);
                        result = Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo);
                    }
                    break;
                case "ExecuteCustomCommand":
                    var custom = request.Data.Deserialize(RemoteJson.Default.CustomCommandRequest)!;
                    var customFingerprint = "custom:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(custom, RemoteJson.Default.CustomCommandRequest)));
                    var customOperation = Register(connection.OwnerId, custom.OperationId, customFingerprint, out var customFresh);
                    if (customFresh) _ = RunCustom(customOperation, custom);
                    result = Protocol.Json(customOperation.Info(), RemoteJson.Default.OperationInfo); break;
                case "GetOperation":
                    var id = request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id;
                    result = Protocol.Json(_operations.TryGetValue(connection.OwnerId + ":" + id, out var found) ? found.Info() : new OperationInfo(id, "unknown"), RemoteJson.Default.OperationInfo); break;
                case "WaitOperation":
                    var waitId = request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id;
                    var waitResult = _operations.TryGetValue(connection.OwnerId + ":" + waitId, out var awaited)
                        ? await awaited.Completion.WaitAsync(cancellation.Token)
                        : new OperationInfo(waitId, "unknown");
                    result = Protocol.Json(waitResult, RemoteJson.Default.OperationInfo); break;
                case "CancelOperation":
                    // Writes are deliberately not force-killed. Cancellation is offered through Git abort operations.
                    throw new InvalidOperationException("Write cancellation requires an explicit Git abort operation");
                case "SubmitInteraction":
                    var reply = request.Data.Deserialize(RemoteJson.Default.InteractionReply)!;
                    if (!_interactions.TryGetValue(reply.Id, out var waiting)) throw new KeyNotFoundException("Interaction expired");
                    waiting.Completion.TrySetResult(reply); result = Protocol.Json("accepted", RemoteJson.Default.String); break;
                default: throw new NotSupportedException("Unknown method: " + request.Method);
            }
            if (BeforeReply != null) await BeforeReply(request.Method, cancellation.Token);
            if (DebugLog) Console.Error.WriteLine($"REQ {request.Method} id={request.Id} {stopwatch?.ElapsedMilliseconds}ms");
            if (DebugLog && request.Method is "GetStatus" or "GetRefs" or "GetHistory" or "GetCommit" or "OpenRepository" or "CloseRepository" or "File" or "ExecuteGit")
            {
                Console.Error.WriteLine($"REQ {request.Method} id={request.Id} {stopwatch?.ElapsedMilliseconds}ms {Truncate(request.Data.GetRawText())}");
            }
            await Reply(connection, request.Id, result, shutdownAccepted ? cancellation.Token : default);
            if (shutdownAccepted) _listener.Stop(); // Result is flushed before stopping the accept loop.
        }
        catch (Exception ex)
        {
            RequestFailed?.Invoke(request.Method, ex);
            if (DebugLog) Console.Error.WriteLine($"REQ {request.Method} id={request.Id} FAILED {stopwatch?.ElapsedMilliseconds}ms: {ex.Message} | {Truncate(request.Data.GetRawText())}");
            if (shutdownAccepted)
            {
                // Accepted shutdown still proceeds if the reply cannot be sent.
                // Closing the channel exposes an unknown acknowledgement outcome.
                try { connection.Client.Close(); } finally { _listener.Stop(); }
            }
            else
                try { await Protocol.WriteAsync(connection.Stream, new Envelope(request.Id, "Result", Protocol.Null, ex.Message), connection.WriteGate); } catch { }
        }
        finally
        {
            connection.Requests.TryRemove(request.Id, out _);
            if (enteredGate) connection.RequestsGate.Release();
            if (enteredOperationWait) { Interlocked.Decrement(ref connection.OperationWaiters); Interlocked.Decrement(ref _operationWaiters); }
        }
    }
    private async Task RunOperation(Operation operation, GitRequest request)
    {
        await operation.ExecuteAsync(_operationTimeout, async token =>
        {
            var result = await _repositories.ExecuteAsync(request, token, operation.TerminationFailed);
            return new(operation.Id, result.ExitCode == 0 ? "succeeded" : "failed", result);
        });
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task RunCustom(Operation operation, CustomCommandRequest request)
    {
        await operation.ExecuteAsync(_operationTimeout, async token =>
        {
            var result = await _repositories.ExecuteCustomAsync(request.RepositoryId, request.Command, operation.Id, line => _ = SendEvent("OperationProgress", Protocol.Json(new OperationProgress(operation.Id, line), RemoteJson.Default.OperationProgress)), token, operation.TerminationFailed);
            return new(operation.Id, result.ExitCode == 0 ? "succeeded" : "failed", result);
        });
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task RunFile(Operation operation, FileRequest request)
    {
        await operation.ExecuteAsync(_operationTimeout, async token => new(operation.Id, "succeeded", FileResult: await _repositories.FileAsync(request, token)));
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task Reply(Connection connection, string id, JsonElement value, CancellationToken cancellation = default)
    {
        var bytes = Encoding.UTF8.GetBytes(value.GetRawText());
        if (bytes.Length < 1024 * 1024) { await Protocol.WriteAsync(connection.Stream, new Envelope(id, "Result", value), connection.WriteGate, cancellation); return; }
        // Chunked JSON responses interleave with heartbeats/interaction, bounded to 256 KiB per frame.
        for (var offset = 0; offset < bytes.Length; offset += 192 * 1024)
            await Protocol.WriteAsync(connection.Stream, new Envelope(id, "Chunk", Protocol.Json(Convert.ToBase64String(bytes.AsSpan(offset, Math.Min(192 * 1024, bytes.Length - offset))), RemoteJson.Default.String)), connection.WriteGate, cancellation);
        await Protocol.WriteAsync(connection.Stream, new Envelope(id, "ChunkEnd", Protocol.Json("", RemoteJson.Default.String)), connection.WriteGate, cancellation);
    }
    private void OnChanged(RepositoryEvent change) => _ = SendEvent("RepositoryChanged", Protocol.Json(change, RemoteJson.Default.RepositoryEvent), $"repo={change.RepositoryId} gen={change.Generation}");
    private async Task SendEvent(string method, JsonElement value, string detail = "", CancellationToken cancellation = default)
    {
        if (DebugLog) Console.Error.WriteLine($"EVT {method} {detail}");
        var destinations = method == "Interaction" ? (_connection == null ? [] : new[] { _connection }) : _channels.Keys.ToArray();
        foreach (var connection in destinations)
        {
            using var writeDeadline = new CancellationTokenSource(_channelGrace);
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellation, writeDeadline.Token);
            try { await Protocol.WriteAsync(connection.Stream, new Envelope("", method, value, Event: true), connection.WriteGate, admission.Token, writeDeadline.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !writeDeadline.IsCancellationRequested)
            {
                // Canceled before frame admission: no bytes were emitted. An
                // admitted frame finishes using the independent write budget.
                return;
            }
            catch (OperationCanceledException) when (writeDeadline.IsCancellationRequested) { connection.Client.Close(); throw; }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
    }
    private async Task HandleHelperAsync(Connection connection, Envelope message)
    {
        var request = message.Data.GetProperty("request").Deserialize(RemoteJson.Default.InteractionRequest)!;
        if (!_operations.TryGetValue(Key(request.OperationId), out var operation) || !message.Data.TryGetProperty("token", out var token) || !SecureEquals(token.GetString() ?? "", operation.HelperToken)) throw new UnauthorizedAccessException("Invalid operation helper credentials");
        var pending = new TaskCompletionSource<InteractionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!operation.EnterInteraction()) throw new InvalidOperationException("Operation no longer accepts interaction");
        _interactions[request.Id] = new PendingInteraction(request, pending);
        try
        {
            await SendEvent("Interaction", Protocol.Json(request, RemoteJson.Default.InteractionRequest), cancellation: operation.ExecutionToken);
            var response = await pending.Task.WaitAsync(_grace, operation.ExecutionToken);
            await Reply(connection, message.Id, Protocol.Json(response, RemoteJson.Default.InteractionReply));
        }
        catch (TimeoutException) { await Reply(connection, message.Id, Protocol.Json(new InteractionReply(request.Id, false, ""), RemoteJson.Default.InteractionReply)); }
        finally { _interactions.TryRemove(request.Id, out _); operation.ExitInteraction(); }
    }
    private static bool SecureEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private string Key(string operationId) => _owner + ":" + operationId;
    private Operation Register(string owner, string id, string fingerprint, out bool fresh)
    {
        lock (_ownerLock)
        {
            if (_owner != owner) throw new IOException("Operation belongs to a replaced owner");
            var operation = _operations.Register(owner + ":" + id, id, fingerprint, out fresh);
            if (fresh)
            {
                _deadlines.OperationChanged();
                _ = NotifyOperationCompleted(operation);
            }
            return operation;
        }
    }
    private async Task NotifyOperationCompleted(Operation operation)
    {
        await operation.Completion.ConfigureAwait(false);
        _deadlines.OperationChanged();
    }
    public void Dispose() { _deadlines.Dispose(); _listener.Stop(); foreach (var timer in _fetchTimers.Values) timer.Dispose(); _directorySnapshots.Dispose(); _repositories.Dispose(); }
}

static class Helpers
{
    public static async Task<string> InstallAsync(string directory)
    {
        var path = Path.Combine(directory, "git-helper-" + Protocol.Build + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        var executable = Environment.ProcessPath!;
        var command = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? "\"" + executable + "\" \"" + typeof(Helpers).Assembly.Location + "\"" : "\"" + executable + "\"";
        await File.WriteAllTextAsync(path, OperatingSystem.IsWindows() ? "@echo off\r\n" + command + " helper %*\r\n" : "#!/bin/sh\nexec " + command + " helper \"$@\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var port = int.Parse(Environment.GetEnvironmentVariable("SOURCEGIT_HELPER_PORT")!);
            var token = Environment.GetEnvironmentVariable("SOURCEGIT_HELPER_TOKEN")!;
            var operation = Environment.GetEnvironmentVariable("SOURCEGIT_OPERATION_ID")!;
            var editor = args.Length == 1 && File.Exists(args[0]);
            var request = new InteractionRequest(Guid.NewGuid().ToString("N"), operation, editor ? "editor" : "credential", editor ? args[0] : string.Join(" ", args), editor ? await File.ReadAllTextAsync(args[0]) : null);
            var json = "{\"token\":" + JsonSerializer.Serialize(token, RemoteJson.Default.String) + ",\"request\":" + JsonSerializer.Serialize(request, RemoteJson.Default.InteractionRequest) + "}";
            using var document = JsonDocument.Parse(json);
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
            using var gate = new SemaphoreSlim(1);
            await Protocol.WriteAsync(client.GetStream(), new Envelope(request.Id, "Helper", document.RootElement.Clone()), gate);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var message = await Protocol.ReadAsync(client.GetStream(), timeout.Token);
            if (message?.Error != null || message == null) return 1;
            var result = message.Data.Deserialize(RemoteJson.Default.InteractionReply)!;
            if (!result.Accepted) return 1;
            if (editor) await File.WriteAllTextAsync(args[0], result.Value); else Console.WriteLine(result.Value);
            return 0;
        }
        catch { return 1; }
    }
}
