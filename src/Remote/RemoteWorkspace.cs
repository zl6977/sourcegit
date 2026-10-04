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
        private readonly ConcurrentQueue<(string Instance, string Id)> _retiredHandles = new();
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
            Connections[id] = this;
            Bind(client);
        }
        private void Bind(RemoteRepositoryClient client)
        {
            Client = client;
            client.RepositoryChanged += Changed;
            client.InteractionRequested += Interaction;
            client.Disconnected += Disconnect;
            Connected = true;
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
        private void Disconnect(Exception error) { Connected = false; ConnectionError?.Invoke(error.Message); }
        public async Task RebindAsync(RemoteRepositoryClient client)
        {
            await _repositoryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Client == client && Connected) return;
                var sameInstance = Client.InstanceId == client.InstanceId;
                var paths = _repositories.Keys.ToArray();
                Client.RepositoryChanged -= Changed;
                Client.InteractionRequested -= Interaction;
                Client.Disconnected -= Disconnect;
                if (!sameInstance) { _repositories.Clear(); _generations.Clear(); }
                Bind(client);
                Environment = await client.GetEnvironmentAsync().ConfigureAwait(false);
                while (_retiredHandles.TryDequeue(out var retired))
                    if (retired.Instance == client.InstanceId) await CloseHandleAsync(retired.Id).ConfigureAwait(false);
                foreach (var path in paths)
                {
                    if (!sameInstance)
                        _repositories[path] = await client.OpenRepositoryAsync(path).ConfigureAwait(false);
                    if (_autoFetch.TryGetValue(path, out var interval))
                        await client.ConfigureAutoFetchAsync(_repositories[path].Id, interval).ConfigureAwait(false);
                    if (_subscriptions.TryGetValue(path, out var update)) update();
                }
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
                    await CloseHandleAsync(result.Id).ConfigureAwait(false);
                    return existing;
                }
                _repositories[result.WorktreePath] = result;
                return result;
            }
            finally { _repositoryGate.Release(); }
        }
        // Commands borrow a handle without a subscription. Each use takes its own server reference
        // (OpenRepository increments References; the Server dedupes by worktree and shares the watcher),
        // so an in-flight command keeps the watcher alive even if a subscription closes, and a
        // command-only open is fully released when the command finishes instead of leaking.
        public Task<RepositoryInfo> OpenTransientAsync(string path, CancellationToken cancellation = default)
            => Client.OpenRepositoryAsync(HostPath(path), cancellation);
        public async Task ReleaseTransientAsync(string id)
        {
            if (id == null) return;
            if (!Connected) { _retiredHandles.Enqueue((Client.InstanceId, id)); return; }
            try { await CloseHandleAsync(id).ConfigureAwait(false); }
            catch (Exception error) { ConnectionError?.Invoke(error.Message); }
        }
        private Task<string> CloseHandleAsync(string id) => Client.CallAsync("CloseRepository", new IdRequest(id), RemoteJson.Default.IdRequest, RemoteJson.Default.String);
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
        public async Task<GitResult> ExecuteCustomAsync(string path, string command, Action<string> output)
        {
            var repository = await OpenTransientAsync(path).ConfigureAwait(false);
            var request = new CustomCommandRequest(repository.Id, command, Guid.NewGuid().ToString("N"));
            var client = Client;
            void Progress(OperationProgress e) { if (e.OperationId == request.OperationId) output?.Invoke(e.Text); }
            client.OperationProgress += Progress;
            try
            {
                var operation = await client.CallAsync("ExecuteCustomCommand", request, RemoteJson.Default.CustomCommandRequest, RemoteJson.Default.OperationInfo).ConfigureAwait(false);
                while (operation.State is "accepted" or "running" or "waitingInteraction")
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    operation = await Client.GetOperationAsync(request.OperationId).ConfigureAwait(false);
                }
                var result = operation.Result ?? throw new System.IO.IOException(operation.Error ?? "Remote custom command outcome is unknown; refresh before retrying");
                if (result.OutputBase64.Length > 0) output?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)));
                return result;
            }
            finally { client.OperationProgress -= Progress; _ = ReleaseTransientAsync(repository.Id); }
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
                    if (Connected) await CloseHandleAsync(repo.Id).ConfigureAwait(false);
                    else _retiredHandles.Enqueue((Client.InstanceId, repo.Id));
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
            RepositoryInfo repo = null;
            string id = null;
            if (args.Count > 0 && args[0] is not ("clone" or "init"))
            {
                try { repo = await connection.OpenTransientAsync(path, cancellation).ConfigureAwait(false); id = repo.Id; }
                catch (System.IO.IOException) when (args[0] == "rev-parse") { }
            }
            var readOnly = IsReadOnly(args);
            var editor = start.Environment.TryGetValue("SOURCEGIT_REMOTE_EDITOR", out var editorValue) ? editorValue : null;
            var sshKey = start.Environment.TryGetValue("SOURCEGIT_REMOTE_SSHKEY", out var sshValue) ? sshValue : null;
            if (RepositoryPath.IsRemote(sshKey)) sshKey = connection.HostPath(sshKey);
            var request = new GitRequest(repo?.Id ?? "", args.ToArray(), readOnly, readOnly ? null : Guid.NewGuid().ToString("N"), input.Length == 0 ? null : Convert.ToBase64String(input), path, editor, sshKey, configs.ToArray(), NoOptionalLocks: noOptionalLocks);
            var client = connection.Client;
            void OnProgress(OperationProgress value)
            {
                if (value.OperationId == request.OperationId) progress?.Invoke(value.Text);
            }
            client.OperationProgress += OnProgress;
            try
            {
                var result = await client.ExecuteGitAsync(request, readOnly ? cancellation : CancellationToken.None).ConfigureAwait(false);
                return new GitExecution(result.ExitCode, Convert.FromBase64String(result.OutputBase64), result.Error, result.Generation);
            }
            finally
            {
                client.OperationProgress -= OnProgress;
                if (id != null) await connection.ReleaseTransientAsync(id).ConfigureAwait(false);
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
            if (args.Count == 0) return false;
            if (new[] { "status", "log", "show", "diff", "diff-files", "diff-index", "ls-files", "ls-tree", "rev-parse", "rev-list", "for-each-ref", "show-ref", "describe", "blame", "cat-file", "check-ignore", "check-attr", "merge-base", "count-objects", "name-rev", "reflog", "version", "ls-remote" }.Contains(args[0])) return true;
            if (args.Count > 1 && args[0] == "branch" && args[1] is "-l" or "--list" or "-a" or "--all" or "--show-current" or "--points-at" or "--contains" or "--no-contains" or "--merged" or "--no-merged" or "--remotes" or "-r" or "--sort" or "--format" or "-v" or "--abbrev" or "--color" or "--no-color") return true;
            if (args.Count > 1 && args[0] == "tag" && args[1] is "-l" or "--list" or "--contains" or "--no-contains" or "--points-at" or "--sort" or "-n" or "--format" or "--color" or "--no-color") return true;
            return args[0] == "config" && args.Any(x => x is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l") || args[0] == "worktree" && args.Contains("list") || args[0] == "remote" && (args.Count == 1 || args[1] is "-v" or "get-url") || args[0] == "stash" && args.Count > 1 && args[1] is "list" or "show";
        }
        public static RemoteFileValue Stat(string path) => FileAsync(path, "stat").GetAwaiter().GetResult();
        public static FileEntry[] List(string path)
        {
            var connection = ForPath(path);
            var result = new List<FileEntry>();
            for (var offset = 0; ; offset += 200)
            {
                var page = connection.Client.ListDirectoryAsync(connection.HostPath(path), offset).GetAwaiter().GetResult();
                result.AddRange(page.Entries ?? []);
                if ((page.Entries?.Length ?? 0) < 200) return result.ToArray();
            }
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
