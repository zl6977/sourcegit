using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.Core;

if (args.Length > 0 && args[0] == "helper") { Environment.Exit(await Helpers.RunAsync(args.Skip(1).ToArray())); return; }
var recordPath = GetOption("--record") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sourcegit-server", "instance-" + Protocol.Build + ".json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(recordPath))!);
using var server = new RemoteServer(recordPath);
await server.RunAsync();
string? GetOption(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

sealed class RemoteServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly RepositoryService _repositories = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _recordPath;
    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private readonly ConcurrentDictionary<string, PendingInteraction> _interactions = new();
    private readonly ConcurrentDictionary<string, Timer> _fetchTimers = new(StringComparer.Ordinal);
    private readonly object _ownerLock = new();
    private string? _owner, _recovery;
    private Connection? _connection;
    private readonly ConcurrentDictionary<Connection, byte> _channels = new();
    private DateTime _lastConnection = DateTime.UtcNow;
    private readonly TimeSpan _grace = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_GRACE_SECONDS"), out var seconds) ? Math.Max(1, seconds) : 300);
    private readonly TimeSpan _channelGrace = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_CHANNEL_GRACE_SECONDS"), out var channelSeconds) ? Math.Max(1, channelSeconds) : 60);
    private static readonly bool DebugLog = Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1";
    private static string Truncate(string text) => text.Length > 400 ? text[..400] : text;
    private string _helperPath = "";
    private sealed class Operation
    {
        public required string Fingerprint;
        public required string Id;
        public string HelperToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string State = "accepted";
        public GitResult? Result;
        public string? Error;
        public FileResult? FileResult;
        public readonly CancellationTokenSource Cancellation = new();
        public OperationInfo Info() => new(Id, State, Result, Error, FileResult);
    }
    private sealed record PendingInteraction(InteractionRequest Request, TaskCompletionSource<InteractionReply> Completion);
    private sealed class Connection
    {
        public required TcpClient Client;
        public required NetworkStream Stream;
        public readonly SemaphoreSlim WriteGate = new(1);
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public readonly ConcurrentDictionary<string, CancellationTokenSource> Requests = new();
        public readonly SemaphoreSlim RequestsGate = new(16);
    }
    public RemoteServer(string recordPath) { _recordPath = recordPath; _repositories.Changed += OnChanged; _repositories.Progress += progress => _ = SendEvent("OperationProgress", Protocol.Json(progress, RemoteJson.Default.OperationProgress)); _repositories.Output = output => SendEvent("OperationOutput", Protocol.Json(output, RemoteJson.Default.OperationOutput)); }
    public async Task RunAsync()
    {
        // An OS file lock makes stale records harmless and protects against concurrent starts.
        using var instanceLock = new FileStream(_recordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
        Console.WriteLine("SOURCEGIT_READY " + _instance);
        // A live client pings every 10 s; a channel silent for 60 s belongs to a dead peer (crash, broken NAT
        // forwarding that never delivered FIN/RST) and must be aborted so its ownership cannot pin the instance.
        using var idle = new Timer(_ => { try { foreach (var connection in _channels.Keys) if (DateTime.UtcNow - connection.LastSeenUtc > _channelGrace) connection.Client.Close(); if (_connection == null && DateTime.UtcNow - _lastConnection > _grace && !_operations.Values.Any(x => x.State is "running" or "waitingInteraction" or "accepted")) _listener.Stop(); } catch { } }, null, 1000, 1000);
        try { while (true) { var client = await _listener.AcceptTcpClientAsync(); _ = HandleAsync(client); } }
        catch (SocketException) { }
        finally { if (File.Exists(_recordPath)) File.Delete(_recordPath); }
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
                    if (_operations.Values.Any(x => x.State is "accepted" or "running" or "waitingInteraction")) throw new InvalidOperationException("Server has active operations; wait for them to finish");
                    _owner = null;
                    foreach (var fetchTimer in _fetchTimers.Values) fetchTimer.Dispose();
                    _fetchTimers.Clear(); _operations.Clear(); _repositories.CloseAll();
                }
                // One GUI owner may use several aliases/channels for the same host; another owner is still rejected.
                _owner = handshake.OwnerId; _recovery = handshake.RecoveryToken; _connection = connection;
                _channels[connection] = 0;
                if (DebugLog) Console.Error.WriteLine($"CHANNEL + owner={handshake.OwnerId} channels={_channels.Count}");
            }
            await Reply(connection, first.Id, Protocol.Json(new HandshakeResult(_instance, Protocol.Version, Protocol.Build, ["git", "files", "watcher", "operations", "interaction", "chunks"]), RemoteJson.Default.HandshakeResult));
            foreach (var pending in _interactions.Values) await Protocol.WriteAsync(connection.Stream, new Envelope("", "Interaction", Protocol.Json(pending.Request, RemoteJson.Default.InteractionRequest), Event: true), connection.WriteGate);
            ready = true;
            while (await Protocol.ReadAsync(connection.Stream) is { } request)
            {
                connection.LastSeenUtc = DateTime.UtcNow;
                if (request.Method == "CancelRequest") { if (connection.Requests.TryGetValue(request.Data.GetString()!, out var cts)) cts.Cancel(); continue; }
                if (connection.Requests.Count > 128 && request.Method is not ("Ping" or "SubmitInteraction"))
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
                if (wasOwnerChannel && _channels.IsEmpty) _lastConnection = DateTime.UtcNow;
                if (DebugLog) Console.Error.WriteLine($"CHANNEL - wasOwner={wasOwnerChannel} channels={_channels.Count} owner={_owner}");
            }
            foreach (var request in connection.Requests.Values) request.Cancel();
            client.Dispose();
        }
    }
    private async Task ProcessAsync(Connection connection, Envelope request)
    {
        var stopwatch = DebugLog ? System.Diagnostics.Stopwatch.StartNew() : null;
        using var cancellation = new CancellationTokenSource();
        connection.Requests[request.Id] = cancellation;
        var needsGate = request.Method is not ("Ping" or "SubmitInteraction" or "GetOperation");
        var enteredGate = false;
        try
        {
            if (needsGate) { await connection.RequestsGate.WaitAsync(cancellation.Token); enteredGate = true; }
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
                    if (_operations.Values.Any(x => x.State is "accepted" or "running" or "waitingInteraction")) throw new InvalidOperationException("Server has active operations; wait for them to finish before restarting");
                    result = Protocol.Json("closing", RemoteJson.Default.String);
                    _ = Task.Run(async () => { await Task.Delay(500); _listener.Stop(); });
                    break;
                case "OpenRepository": result = Protocol.Json(await _repositories.OpenAsync(request.Data.Deserialize(RemoteJson.Default.OpenRequest)!.Path), RemoteJson.Default.RepositoryInfo); break;
                case "CloseRepository":
                    var closeId = request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id;
                    var closeCommon = _repositories.Get(closeId).CommonDirectory;
                    _repositories.Close(closeId);
                    if (_repositories.FindByCommonDirectory(closeCommon) == null && _fetchTimers.TryRemove(closeCommon, out var closingTimer)) closingTimer.Dispose();
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
                                    var operation = new Operation { Id = id, Fingerprint = "autofetch" };
                                    _operations[Key(id)] = operation;
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
                    result = Protocol.Json(new FileResult(Entries: Directory.EnumerateFileSystemEntries(Path.GetFullPath(directory.Path)).Order(StringComparer.Ordinal).Skip(Math.Max(0, directory.Offset)).Take(Math.Clamp(directory.Count, 1, 1000)).Select(RepositoryService.Entry).ToArray()), RemoteJson.Default.FileResult); break;
                case "File":
                    var fileRequest = request.Data.Deserialize(RemoteJson.Default.FileRequest)!;
                    if (fileRequest.Action is "stat" or "exists" or "read" or "list" or "hash") result = Protocol.Json(await _repositories.FileAsync(fileRequest), RemoteJson.Default.FileResult);
                    else
                    {
                        if (string.IsNullOrWhiteSpace(fileRequest.OperationId)) throw new ArgumentException("File writes require client-generated operation ID");
                        var fileFingerprint = "file:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fileRequest, RemoteJson.Default.FileRequest)));
                        var fileFresh = new Operation { Id = fileRequest.OperationId, Fingerprint = fileFingerprint };
                        if (_operations.Count >= 100000 && !_operations.ContainsKey(Key(fileRequest.OperationId))) throw new IOException("Operation history capacity reached; reconnect to a fresh server when idle");
                        var fileOperation = _operations.GetOrAdd(Key(fileRequest.OperationId), fileFresh);
                        if (fileOperation.Fingerprint != fileFingerprint) throw new InvalidOperationException("Operation ID reused with different parameters");
                        if (ReferenceEquals(fileOperation, fileFresh)) _ = RunFile(fileOperation, fileRequest);
                        while (fileOperation.State is "accepted" or "running") await Task.Delay(20, cancellation.Token);
                        result = Protocol.Json(fileOperation.FileResult ?? throw new IOException(fileOperation.Error ?? "File operation outcome unknown"), RemoteJson.Default.FileResult);
                    }
                    break;
                case "ExecuteGit":
                    var gitRequest = request.Data.Deserialize(RemoteJson.Default.GitRequest)!;
                    if (gitRequest.ReadOnly) result = Protocol.Json(await _repositories.ExecuteAsync(gitRequest, cancellation.Token), RemoteJson.Default.GitResult);
                    else
                    {
                        if (string.IsNullOrWhiteSpace(gitRequest.OperationId)) throw new ArgumentException("Client-generated operation ID required");
                        GitPolicy.Validate(gitRequest.Arguments, false);
                        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(gitRequest, RemoteJson.Default.GitRequest)));
                        var fresh = new Operation { Id = gitRequest.OperationId, Fingerprint = fingerprint };
                        if (_operations.Count >= 100000 && !_operations.ContainsKey(Key(gitRequest.OperationId))) throw new IOException("Operation history capacity reached; reconnect to a fresh server when idle");
                        var operation = _operations.GetOrAdd(Key(gitRequest.OperationId), fresh);
                        if (operation.Fingerprint != fingerprint) throw new InvalidOperationException("Operation ID reused with different parameters");
                        if (ReferenceEquals(operation, fresh)) _ = RunOperation(operation, gitRequest);
                        result = Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo);
                    }
                    break;
                case "ExecuteCustomCommand":
                    var custom = request.Data.Deserialize(RemoteJson.Default.CustomCommandRequest)!;
                    var customFingerprint = "custom:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(custom, RemoteJson.Default.CustomCommandRequest)));
                    var customFresh = new Operation { Id = custom.OperationId, Fingerprint = customFingerprint };
                    var customOperation = _operations.GetOrAdd(Key(custom.OperationId), customFresh);
                    if (customOperation.Fingerprint != customFingerprint) throw new InvalidOperationException("Operation ID reused with different parameters");
                    if (ReferenceEquals(customFresh, customOperation)) _ = RunCustom(customOperation, custom);
                    result = Protocol.Json(customOperation.Info(), RemoteJson.Default.OperationInfo); break;
                case "GetOperation":
                    var id = request.Data.Deserialize(RemoteJson.Default.IdRequest)!.Id;
                    result = Protocol.Json(_operations.TryGetValue(Key(id), out var found) ? found.Info() : new OperationInfo(id, "unknown"), RemoteJson.Default.OperationInfo); break;
                case "CancelOperation":
                    // Writes are deliberately not force-killed. Cancellation is offered through Git abort operations.
                    throw new InvalidOperationException("Write cancellation requires an explicit Git abort operation");
                case "SubmitInteraction":
                    var reply = request.Data.Deserialize(RemoteJson.Default.InteractionReply)!;
                    if (!_interactions.TryGetValue(reply.Id, out var waiting)) throw new KeyNotFoundException("Interaction expired");
                    waiting.Completion.TrySetResult(reply); result = Protocol.Json("accepted", RemoteJson.Default.String); break;
                default: throw new NotSupportedException("Unknown method: " + request.Method);
            }
            if (DebugLog) Console.Error.WriteLine($"REQ {request.Method} id={request.Id} {stopwatch?.ElapsedMilliseconds}ms");
            if (DebugLog && request.Method is "GetStatus" or "GetRefs" or "GetHistory" or "GetCommit" or "OpenRepository" or "CloseRepository" or "File" or "ExecuteGit")
            {
                Console.Error.WriteLine($"REQ {request.Method} id={request.Id} {stopwatch?.ElapsedMilliseconds}ms {Truncate(request.Data.GetRawText())}");
            }
            await Reply(connection, request.Id, result);
        }
        catch (Exception ex)
        {
            if (DebugLog) Console.Error.WriteLine($"REQ {request.Method} id={request.Id} FAILED {stopwatch?.ElapsedMilliseconds}ms: {ex.Message} | {Truncate(request.Data.GetRawText())}");
            try { await Protocol.WriteAsync(connection.Stream, new Envelope(request.Id, "Result", Protocol.Null, ex.Message), connection.WriteGate); } catch { }
        }
        finally { connection.Requests.TryRemove(request.Id, out _); if (enteredGate) connection.RequestsGate.Release(); }
    }
    private async Task RunOperation(Operation operation, GitRequest request)
    {
        operation.State = "running";
        try { operation.Result = await _repositories.ExecuteAsync(request); operation.State = operation.Result.ExitCode == 0 ? "succeeded" : "failed"; }
        catch (Exception ex) { operation.Error = ex.Message; operation.State = "failed"; }
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task RunCustom(Operation operation, CustomCommandRequest request)
    {
        operation.State = "running";
        try
        {
            operation.Result = await _repositories.ExecuteCustomAsync(request.RepositoryId, request.Command, operation.Id, line => _ = SendEvent("OperationProgress", Protocol.Json(new OperationProgress(operation.Id, line), RemoteJson.Default.OperationProgress)));
            operation.State = operation.Result.ExitCode == 0 ? "succeeded" : "failed";
        }
        catch (Exception ex) { operation.Error = ex.Message; operation.State = "failed"; }
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task RunFile(Operation operation, FileRequest request)
    {
        operation.State = "running";
        try { operation.FileResult = await _repositories.FileAsync(request); operation.State = "succeeded"; }
        catch (Exception ex) { operation.Error = ex.Message; operation.State = "failed"; }
        await SendEvent("OperationCompleted", Protocol.Json(operation.Info(), RemoteJson.Default.OperationInfo));
    }
    private async Task Reply(Connection connection, string id, JsonElement value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.GetRawText());
        if (bytes.Length < 1024 * 1024) { await Protocol.WriteAsync(connection.Stream, new Envelope(id, "Result", value), connection.WriteGate); return; }
        // Chunked JSON responses interleave with heartbeats/interaction, bounded to 256 KiB per frame.
        for (var offset = 0; offset < bytes.Length; offset += 192 * 1024)
            await Protocol.WriteAsync(connection.Stream, new Envelope(id, "Chunk", Protocol.Json(Convert.ToBase64String(bytes.AsSpan(offset, Math.Min(192 * 1024, bytes.Length - offset))), RemoteJson.Default.String)), connection.WriteGate);
        await Protocol.WriteAsync(connection.Stream, new Envelope(id, "ChunkEnd", Protocol.Json("", RemoteJson.Default.String)), connection.WriteGate);
    }
    private void OnChanged(RepositoryEvent change) => _ = SendEvent("RepositoryChanged", Protocol.Json(change, RemoteJson.Default.RepositoryEvent), $"repo={change.RepositoryId} gen={change.Generation}");
    private async Task SendEvent(string method, JsonElement value, string detail = "")
    {
        if (DebugLog) Console.Error.WriteLine($"EVT {method} {detail}");
        var destinations = method == "Interaction" ? (_connection == null ? [] : new[] { _connection }) : _channels.Keys.ToArray();
        foreach (var connection in destinations)
        { try { await Protocol.WriteAsync(connection.Stream, new Envelope("", method, value, Event: true), connection.WriteGate); } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { } }
    }
    private async Task HandleHelperAsync(Connection connection, Envelope message)
    {
        var request = message.Data.GetProperty("request").Deserialize(RemoteJson.Default.InteractionRequest)!;
        if (!_operations.TryGetValue(Key(request.OperationId), out var operation) || !message.Data.TryGetProperty("token", out var token) || !SecureEquals(token.GetString() ?? "", operation.HelperToken)) throw new UnauthorizedAccessException("Invalid operation helper credentials");
        var pending = new TaskCompletionSource<InteractionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _interactions[request.Id] = new PendingInteraction(request, pending);
        operation.State = "waitingInteraction";
        try
        {
            await SendEvent("Interaction", Protocol.Json(request, RemoteJson.Default.InteractionRequest));
            var response = await pending.Task.WaitAsync(_grace);
            await Reply(connection, message.Id, Protocol.Json(response, RemoteJson.Default.InteractionReply));
        }
        catch (TimeoutException) { await Reply(connection, message.Id, Protocol.Json(new InteractionReply(request.Id, false, ""), RemoteJson.Default.InteractionReply)); }
        finally { _interactions.TryRemove(request.Id, out _); operation.State = "running"; }
    }
    private static bool SecureEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private string Key(string operationId) => _owner + ":" + operationId;
    public void Dispose() { _listener.Stop(); foreach (var timer in _fetchTimers.Values) timer.Dispose(); _repositories.Dispose(); }
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
