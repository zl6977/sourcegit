using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

namespace SourceGit.Remote
{
    public sealed record RemoteFileValue(byte[] Content, FileEntry[] Entries, bool Exists, bool Directory, long Length, DateTime LastWriteTimeUtc, string Hash = null);
    public sealed class RemoteWorkspace
    {
        private static readonly ConcurrentDictionary<string, RemoteWorkspace> Connections = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RepositoryInfo> _repositories = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Action> _subscriptions = new();
        private readonly ConcurrentDictionary<string, long> _generations = new();
        private readonly SemaphoreSlim _repositoryGate = new(1);
        private readonly ConcurrentDictionary<string, int> _autoFetch = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Instance, string LeaseId)> _retiredHandles = new();
        private Action<Exception> _disconnected;
        internal int PendingReleaseCount => _retiredHandles.Count;
        public string Id { get; }
        public string Name { get; }
        public RemoteRepositoryClient Client { get; private set; }
        public EnvironmentInfo Environment { get; private set; }
        public bool Connected { get; private set; } = true;
        public event Action<string> ConnectionError;
        public static Func<InteractionRequest, Task<InteractionReply>> InteractAsync { get; set; }
        public RemoteWorkspace(string id, string name, RemoteRepositoryClient client, EnvironmentInfo environment)
        {
            Id = id; Name = name; Client = client; Environment = environment;
            Bind(client);
            Connections[id] = this;
        }
        private void Bind(RemoteRepositoryClient client, bool connected = true)
        {
            Client = client;
            client.RepositoryChanged += Changed;
            client.InteractionRequested += Interaction;
            _disconnected = error => { if (Client == client) Disconnect(error); };
            client.Disconnected += _disconnected;
            Connected = connected;
        }
        private void Changed(RepositoryEvent e)
        {
            while (true)
            {
                var old = _generations.GetOrAdd(e.RepositoryId, -1);
                if (e.Generation <= old) return;
                if (_generations.TryUpdate(e.RepositoryId, e.Generation, old)) break;
            }
            foreach (var path in _repositories.Where(x => x.Value.Id == e.RepositoryId).Select(x => x.Key))
                if (_subscriptions.TryGetValue(path, out var update)) update();
        }
        private void Interaction(InteractionRequest request) => _ = ReplyAsync(request);
        internal void Unregister()
        {
            Connected = false;
            // An old instance closing must not remove a newer registration.
            ((ICollection<KeyValuePair<string, RemoteWorkspace>>)Connections).Remove(new(Id, this));
        }
        private void Disconnect(Exception error) { Unregister(); ConnectionError?.Invoke(error.Message); }
        public async Task RebindAsync(RemoteRepositoryClient client)
        {
            await _repositoryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Client == client && Connected)
                {
                    await DrainRetiredHandlesAsync(client).ConfigureAwait(false);
                    return;
                }
                var sameInstance = Client.InstanceId == client.InstanceId;
                var paths = _repositories.Keys.ToArray();
                Client.RepositoryChanged -= Changed;
                Client.InteractionRequested -= Interaction;
                Client.Disconnected -= _disconnected;
                if (!sameInstance) { _repositories.Clear(); _generations.Clear(); }
                Bind(client, false);
                Environment = await client.GetEnvironmentAsync().ConfigureAwait(false);
                await DrainRetiredHandlesAsync(client).ConfigureAwait(false);
                foreach (var path in paths)
                {
                    if (!sameInstance)
                        _repositories[path] = await client.OpenRepositoryAsync(path).ConfigureAwait(false);
                    if (_autoFetch.TryGetValue(path, out var interval))
                        await client.ConfigureAutoFetchAsync(_repositories[path].Id, interval).ConfigureAwait(false);
                }
                Connected = !client.IsDisconnected;
                if (Connected) Connections.TryAdd(Id, this);
                foreach (var path in paths)
                    if (_subscriptions.TryGetValue(path, out var update)) update();
            }
            finally { _repositoryGate.Release(); }
        }
        public static RemoteWorkspace ForPath(string path)
        {
            if (!Connections.TryGetValue(RepositoryPath.ConnectionId(path), out var connection))
                throw new System.IO.IOException("Reconnect to the remote environment before opening this repository");
            if (!connection.Connected) throw new System.IO.IOException("The remote connection is disconnected");
            return connection;
        }
        public static bool IsConnected(string path) => RepositoryPath.IsRemote(path) && Connections.TryGetValue(RepositoryPath.ConnectionId(path), out var value) && value.Connected;
        public static Version GitVersion(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return Native.OS.GitVersion;
            var text = ForPath(path).Environment.GitVersion;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = word.Split('.');
                if (parts.Length >= 3 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
                {
                    var patch = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(patch, out var build)) return new Version(major, minor, build);
                }
            }
            throw new System.IO.IOException("Server did not return a valid Git version");
        }
        public static Models.GitFlowVersion GitFlowVersion(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return Native.OS.GitFlowVersion;
            var capabilities = ForPath(path).Environment.Capabilities;
            return capabilities.Contains("git-flow-next") ? Models.GitFlowVersion.Next :
                capabilities.Contains("git-flow") ? Models.GitFlowVersion.Legacy : Models.GitFlowVersion.None;
        }
        public async Task<(RepositoryInfo Repository, string Directory)> OpenLocationAsync(string path, CancellationToken cancellation = default)
        {
            try
            {
                var repository = await OpenAsync(path, cancellation).ConfigureAwait(false);
                return (repository, repository.WorktreePath);
            }
            catch (System.IO.IOException error) when (error.Message.Contains("fatal: not a git repository", StringComparison.Ordinal))
            {
                var directory = await Client.FileAsync(new FileRequest("", "stat", HostPath(path)), cancellation).ConfigureAwait(false);
                if (!directory.Exists || !directory.Directory) throw;
                var canonical = directory.Entries[0].Path;
                var metadata = await Client.FileAsync(new FileRequest("", "stat", canonical.TrimEnd('/', '\\') + "/.git"), cancellation).ConfigureAwait(false);
                // Invalid repository metadata must remain an error, never an invitation to overwrite it.
                if (metadata.Exists) throw;
                return (null, canonical);
            }
        }

        public async Task<RepositoryInfo> OpenAsync(string path, CancellationToken cancellation = default)
        {
            var remote = HostPath(path);
            await _repositoryGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (_repositories.TryGetValue(remote, out var existing)) return existing;
                var result = await Client.OpenRepositoryAsync(remote, cancellation).ConfigureAwait(false);
                if (_repositories.TryGetValue(result.WorktreePath, out existing))
                {
                    await CloseOrQueueHandleAsync(result.LeaseId, Client.InstanceId).ConfigureAwait(false);
                    return existing;
                }
                _repositories[result.WorktreePath] = result;
                return result;
            }
            finally { _repositoryGate.Release(); }
        }
        // Commands borrow a handle without a subscription. Each open owns a unique lease
        // (the Server shares a repository session and watcher among its live leases),
        // so an in-flight command keeps the watcher alive even if a subscription closes, and a
        // command-only open is fully released when the command finishes instead of leaking.
        public Task<RepositoryInfo> OpenTransientAsync(string path, CancellationToken cancellation = default)
            => Client.OpenRepositoryAsync(HostPath(path), cancellation);
        public async Task ReleaseTransientAsync(RepositoryInfo repository, RemoteRepositoryClient originalClient = null)
        {
            if (repository == null) return;
            var instance = (originalClient ?? Client).InstanceId;
            await _repositoryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseOrQueueHandleAsync(repository.LeaseId, instance).ConfigureAwait(false);
            }
            finally { _repositoryGate.Release(); }
        }
        private async Task CloseOrQueueHandleAsync(string leaseId, string instance)
        {
            if (instance != Client.InstanceId) return;
            if (!Connected || Client.IsDisconnected) { _retiredHandles.Enqueue((instance, leaseId)); return; }
            try { await Client.CloseRepositoryAsync(leaseId).ConfigureAwait(false); }
            catch (Exception error)
            {
                // The close may already have executed. Lease ids make a later retry
                // safe, so online failures must retain the entry as well as disconnects.
                _retiredHandles.Enqueue((instance, leaseId));
                ConnectionError?.Invoke("Repository lease close queued for retry: " + error.Message);
            }
        }
        private async Task DrainRetiredHandlesAsync(RemoteRepositoryClient client)
        {
            // Retry each entry at most once per drain. Failed entries rejoin the
            // queue without being immediately retried in an endless loop.
            var count = _retiredHandles.Count;
            for (var i = 0; i < count && _retiredHandles.TryDequeue(out var retired); i++)
            {
                if (retired.Instance != client.InstanceId) continue;
                try { await client.CloseRepositoryAsync(retired.LeaseId).ConfigureAwait(false); }
                catch (Exception error)
                {
                    _retiredHandles.Enqueue(retired);
                    ConnectionError?.Invoke("Repository lease close queued for retry: " + error.Message);
                    if (client.IsDisconnected) break;
                }
            }
        }
        public async Task ConfigureAutoFetchAsync(string path, int interval)
        {
            var repo = await OpenAsync(path).ConfigureAwait(false);
            _autoFetch[repo.WorktreePath] = interval;
            await Client.ConfigureAutoFetchAsync(repo.Id, interval).ConfigureAwait(false);
        }
        private bool WindowsHost => Environment.OS.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
        private string HostPath(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return path;
            var raw = RepositoryPath.RemotePath(path);
            if (WindowsHost && raw.Length >= 4 && raw[0] == '/' && char.IsLetter(raw[1]) && raw[2] == ':')
                return raw[1..].Replace('/', '\\');
            return raw;
        }
        public string Resource(string path) => RepositoryPath.Remote(Id, WindowsHost ? path.Replace('\\', '/') : path);
        public string ExecutionPath(string path) => HostPath(path);
        public bool AcceptSnapshot(string path, RepositorySnapshot snapshot, RepositorySnapshotTracker tracker)
        {
            return Connected && _repositories.TryGetValue(HostPath(path), out var repository) &&
                tracker.TryAccept(snapshot, Client.InstanceId, repository.Id);
        }
        public static bool TryAcceptSnapshot(string path, RepositorySnapshot snapshot, RepositorySnapshotTracker tracker)
            => Connections.TryGetValue(RepositoryPath.ConnectionId(path), out var workspace) && workspace.AcceptSnapshot(path, snapshot, tracker);
        public async Task<GitResult> ExecuteCustomAsync(string path, string command, Action<string> output)
        {
            var client = Client;
            var repository = await client.OpenRepositoryAsync(HostPath(path)).ConfigureAwait(false);
            var request = new CustomCommandRequest(repository.Id, command, Guid.NewGuid().ToString("N"));
            try
            {
                var result = await client.ExecuteCustomAsync(request, progress: e => output?.Invoke(e.Text)).ConfigureAwait(false);
                if (result.OutputBase64.Length > 0) output?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)));
                return result;
            }
            finally { await ReleaseTransientAsync(repository, client).ConfigureAwait(false); }
        }
        private async Task ReplyAsync(InteractionRequest request)
        {
            try
            {
                var reply = InteractAsync != null ? await InteractAsync(request).ConfigureAwait(false) : new InteractionReply(request.Id, false, "");
                await Client.SubmitInteractionAsync(reply).ConfigureAwait(false);
            }
            catch (Exception error) { ConnectionError?.Invoke(error.Message); }
        }
        public IDisposable Subscribe(string path, Action changed)
        {
            var repo = OpenAsync(path).GetAwaiter().GetResult();
            _subscriptions.AddOrUpdate(repo.WorktreePath, changed, (_, previous) => previous + changed);
            return new Subscription(() => _ = UnsubscribeAsync(repo.WorktreePath, changed));
        }
        private async Task UnsubscribeAsync(string path, Action changed)
        {
            await _repositoryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_subscriptions.TryGetValue(path, out var previous)) return;
                var remaining = previous - changed;
                if (remaining != null) { _subscriptions[path] = remaining; return; }
                _subscriptions.TryRemove(path, out _);
                _autoFetch.TryRemove(path, out _);
                if (_repositories.TryRemove(path, out var repo))
                {
                    await CloseOrQueueHandleAsync(repo.LeaseId, Client.InstanceId).ConfigureAwait(false);
                }
            }
            catch (Exception error) { ConnectionError?.Invoke(error.Message); }
            finally { _repositoryGate.Release(); }
        }
        public static async Task<GitExecution> ExecuteGitAsync(ProcessStartInfo start, byte[] input, CancellationToken cancellation, Action<string> progress = null)
        {
            var connection = ForPath(start.WorkingDirectory);
            var args = Tokenize(start.Arguments);
            var configs = new List<string>();
            var noOptionalLocks = args.Count > 0 && args[0] == "--no-optional-locks";
            if (noOptionalLocks) args.RemoveAt(0);
            while (args.Count >= 2 && args[0] == "-c")
            {
                if (args[1] != "core.editor=true") configs.Add(args[1]);
                args.RemoveRange(0, 2);
            }
            for (var i = 0; i < args.Count; i++)
            {
                if (RepositoryPath.IsRemote(args[i])) args[i] = connection.HostPath(args[i]);
                else
                {
                    var equal = args[i].IndexOf('=');
                    if (equal >= 0 && RepositoryPath.IsRemote(args[i][(equal + 1)..])) args[i] = args[i][..(equal + 1)] + connection.HostPath(args[i][(equal + 1)..]);
                }
            }
            // Old update-index call sites set only this fixed editor config; never forward global -c overrides.
            if (args.Count >= 2 && args[0] == "-c" && args[1] == "core.editor=true") args.RemoveRange(0, 2);
            var path = connection.HostPath(start.WorkingDirectory);
            var client = connection.Client;
            RepositoryInfo repo = null;
            if (args.Count > 0 && args[0] is not ("clone" or "init"))
            {
                try { repo = await client.OpenRepositoryAsync(path, cancellation).ConfigureAwait(false); }
                catch (System.IO.IOException) when (args[0] == "rev-parse") { }
            }
            var readOnly = IsReadOnly(args);
            var editor = start.Environment.TryGetValue("SOURCEGIT_REMOTE_EDITOR", out var editorValue) ? editorValue : null;
            var sshKey = start.Environment.TryGetValue("SOURCEGIT_REMOTE_SSHKEY", out var sshValue) ? sshValue : null;
            if (RepositoryPath.IsRemote(sshKey)) sshKey = connection.HostPath(sshKey);
            var request = new GitRequest(repo?.Id ?? "", args.ToArray(), readOnly, readOnly ? null : Guid.NewGuid().ToString("N"), input.Length == 0 ? null : Convert.ToBase64String(input), path, editor, sshKey, configs.ToArray(), NoOptionalLocks: noOptionalLocks);
            try
            {
                var result = await client.ExecuteGitAsync(request, readOnly ? cancellation : CancellationToken.None, value => progress?.Invoke(value.Text)).ConfigureAwait(false);
                return new GitExecution(result.ExitCode, Convert.FromBase64String(result.OutputBase64), result.Error, result.Generation);
            }
            finally
            {
                if (repo != null) await connection.ReleaseTransientAsync(repo, client).ConfigureAwait(false);
            }
        }
        public static List<string> Tokenize(string command)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            var started = false;
            for (var i = 0; i < command.Length; i++)
            {
                var c = command[i];
                if (c == '\\' && i + 1 < command.Length && command[i + 1] == '"') { current.Append('"'); i++; started = true; }
                else if (c == '"') { quoted = !quoted; started = true; }
                else if (char.IsWhiteSpace(c) && !quoted) { if (started) { parts.Add(current.ToString()); current.Clear(); started = false; } }
                else { current.Append(c); started = true; }
            }
            if (quoted) throw new ArgumentException("Unterminated quoted Git argument");
            if (started) parts.Add(current.ToString());
            return parts;
        }
        private static bool IsReadOnly(List<string> args)
        {
            return GitCommandPolicy.IsQuery(args.ToArray());
        }
        public static RemoteFileValue Stat(string path) => FileAsync(path, "stat").GetAwaiter().GetResult();
        public static FileEntry[] List(string path)
            => ListAsync(path).GetAwaiter().GetResult();
        public static async Task<FileEntry[]> ListAsync(string path, CancellationToken cancellation = default)
        {
            var connection = ForPath(path);
            var result = new List<FileEntry>();
            await foreach (var page in connection.Client.DirectoryPagesAsync(connection.HostPath(path), cancellation).ConfigureAwait(false))
            {
                result.AddRange(page);
            }
            return result.ToArray();
        }
        public static async Task<RemoteFileValue> FileAsync(string path, string action, string destination = null, byte[] content = null, long offset = 0, int count = 64 * 1024, bool overwrite = false, bool truncate = false, bool createNew = false, CancellationToken cancellation = default, string expectedHash = null)
        {
            var connection = ForPath(path);
            var raw = connection.HostPath(path);
            var repo = connection._repositories.Values.FirstOrDefault(x => raw == x.WorktreePath || raw.StartsWith(x.WorktreePath.TrimEnd('/') + "/", StringComparison.Ordinal) || raw == x.GitDirectory || raw.StartsWith(x.GitDirectory.TrimEnd('/') + "/", StringComparison.Ordinal) || raw.StartsWith(x.CommonDirectory.TrimEnd('/') + "/", StringComparison.Ordinal));
            var request = new FileRequest(repo?.Id ?? "", action, raw, destination == null ? null : connection.HostPath(destination), content == null ? null : Convert.ToBase64String(content), offset, count, ExpectedHash: expectedHash, Overwrite: overwrite, Truncate: truncate, CreateNew: createNew);
            var value = await connection.Client.FileAsync(request, cancellation).ConfigureAwait(false);
            return new RemoteFileValue(value.ContentBase64 == null ? [] : Convert.FromBase64String(value.ContentBase64), value.Entries ?? [], value.Exists, value.Directory, value.Length, value.LastWriteTimeUtc, value.Hash);
        }
        private sealed class Subscription(Action unsubscribe) : IDisposable
        {
            private Action _unsubscribe = unsubscribe;
            public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
