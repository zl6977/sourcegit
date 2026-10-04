using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.RemoteClient;

namespace SourceGit.Remote
{
    public static class RemoteConnections
    {
        private static readonly ConcurrentDictionary<string, ConnectionManager> Managers = new();
        private static readonly ConcurrentDictionary<string, RemoteWorkspace> Workspaces = new();
        private static readonly SemaphoreSlim Gate = new(1);
        private static string SettingsFile => Path.Combine(Native.OS.BasicDirectories.ConfigDir, "remote-connections.json");
        public static ConnectionOptions[] Load()
        {
            try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), ConnectionJson.Default.ConnectionOptionsArray) ?? [] : []; }
            catch (Exception error) { Native.OS.LogException(error); return []; }
        }
        public static void Save(ConnectionOptions options)
        {
            var values = Load();
            // The same transport + target + user is the same connection: reusing the saved Id keeps the list duplicate-free
            // and keeps repository nodes (which embed the connection Id) pointing at the surviving entry.
            if (!values.Any(x => x.Id == options.Id) && values.FirstOrDefault(x => SameIdentity(x, options.Kind, options.Target, options.User)) is { } twin)
                options = options with { Id = twin.Id };
            values = values.Where(x => x.Id != options.Id).Append(options).ToArray();
            var temporary = SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values, ConnectionJson.Default.ConnectionOptionsArray));
            File.Move(temporary, SettingsFile, true);
        }
        // Same physical endpoint: same transport, same target (host aliases and WSL distro names resolve case-insensitively),
        // same user (user names are case-sensitive). Package/Id differences are deployment details, not identity.
        public static bool SameIdentity(ConnectionOptions saved, string kind, string target, string user) =>
            saved.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
            saved.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            (saved.User ?? "") == (user ?? "");
        // Display-only friendly path for WSL resources: \\wsl.localhost\<distro>/<host path>.
        // Remote URIs map through the saved connection's distro; local //wsl.localhost UNC paths parse the distro in place.
        // The UNC header keeps backslashes (UNC syntax); the Linux part uses forward slashes (Linux style),
        // which Windows accepts interchangeably after the UNC header.
        // Never use the result for routing or I/O; the canonical form (sourcegit-remote:// URI or stored local path) stays unchanged.
        public static string DisplayPath(string path) => WslDisplayPath(path, LoadCached());
        // Pure core: the \\wsl.localhost UNC display form for WSL resources, given the saved connection options.
        public static string WslDisplayPath(string path, ConnectionOptions[] options)
        {
            if (!OperatingSystem.IsWindows()) return null;
            string distro = null, hostPath = null;
            if (RepositoryPath.IsRemote(path))
            {
                var option = options.FirstOrDefault(x => x.Id == RepositoryPath.ConnectionId(path));
                if (option is null || !option.Kind.Equals("Wsl", StringComparison.OrdinalIgnoreCase)) return null;
                distro = option.Target;
                hostPath = RepositoryPath.RemotePath(path);
            }
            else if (path.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = path.Substring(16);
                var slash = rest.IndexOf('/');
                distro = slash > 0 ? rest[..slash] : rest;
                hostPath = slash > 0 ? rest[slash..] : null;
            }
            else
                return null;
            if (string.IsNullOrEmpty(distro) || distro.IndexOfAny('\\', '/') >= 0 || string.IsNullOrEmpty(hostPath)) return null;
            return @"\\wsl.localhost\" + distro + hostPath;
        }
        // The other access route for the same physical WSL repository, in stored form: a remote URI on a WSL
        // connection and the local //wsl.localhost UNC path are two routes to one repository — the history must
        // keep a single node for it. Null when the path has no cross-route twin (non-WSL, unknown connection, bare distro).
        public static string WslTwinId(string path, ConnectionOptions[] options)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (RepositoryPath.IsRemote(path))
            {
                var option = options.FirstOrDefault(x => x.Id == RepositoryPath.ConnectionId(path));
                if (option is null || !option.Kind.Equals("Wsl", StringComparison.OrdinalIgnoreCase)) return null;
                return "//wsl.localhost/" + option.Target + RepositoryPath.RemotePath(path);
            }
            if (path.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = path.Substring(16);
                var slash = rest.IndexOf('/');
                var distro = slash > 0 ? rest[..slash] : rest;
                var remotePath = slash > 0 ? rest[slash..] : null;
                if (string.IsNullOrEmpty(distro) || string.IsNullOrEmpty(remotePath)) return null;
                var option = options.FirstOrDefault(x => x.Kind.Equals("Wsl", StringComparison.OrdinalIgnoreCase) && x.Target.Equals(distro, StringComparison.OrdinalIgnoreCase));
                return option == null ? null : RepositoryPath.Remote(option.Id, remotePath);
            }
            return null;
        }
        // Route preference for WSL repositories: when a saved WSL connection exists for the distro, the
        // //wsl.localhost UNC form resolves to its remote-URI twin. The server route is the only one with
        // event-driven repository updates (server-side file watchers); Windows file watchers on the WSLg
        // share receive no change notifications at all. Non-WSL, already-remote and connection-less paths
        // are returned unchanged.
        public static string PreferredRoute(string path, ConnectionOptions[] options)
        {
            if (RepositoryPath.IsRemote(path)) return path;
            return WslTwinId(path, options) ?? path;
        }
        private static readonly object DisplayLock = new();
        private static DateTime _displayCacheTime;
        private static ConnectionOptions[] _displayCache = [];
        public static ConnectionOptions[] LoadCached()
        {
            lock (DisplayLock)
            {
                var time = File.Exists(SettingsFile) ? File.GetLastWriteTimeUtc(SettingsFile) : DateTime.MinValue;
                if (time != _displayCacheTime)
                {
                    _displayCacheTime = time;
                    _displayCache = Load();
                }
                return _displayCache;
            }
        }
        public static async Task<RemoteWorkspace> ConnectAsync(ConnectionOptions options, Action<ConnectionDiagnostic> diagnostic = null, CancellationToken cancellation = default)
        {
            await Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (Managers.TryGetValue(options.Id, out var oldManager) && oldManager.Options != options)
                {
                    await oldManager.DisposeAsync().ConfigureAwait(false);
                    Managers.TryRemove(options.Id, out _);
                }
                if (Managers.ContainsKey(options.Id) && Workspaces.TryGetValue(options.Id, out var connected) && connected.Connected) return connected;
                if (!Managers.TryGetValue(options.Id, out var manager))
                {
                    manager = new ConnectionManager(options);
                    Managers[options.Id] = manager;
                    manager.Connected += (client, instanceChanged) =>
                    {
                        if (Workspaces.TryGetValue(options.Id, out var existing))
                            _ = RebindAsync(existing, client);
                    };
                }
                if (diagnostic != null) manager.Diagnostic += diagnostic;
                try
                {
                    var client = await manager.ConnectAsync(cancellation).ConfigureAwait(false);
                    if (Workspaces.TryGetValue(options.Id, out var existing))
                    {
                        if (existing.Client != client) await existing.RebindAsync(client).ConfigureAwait(false);
                        return existing;
                    }
                    var environment = await client.GetEnvironmentAsync(cancellation).ConfigureAwait(false);
                    var workspace = new RemoteWorkspace(options.Id, options.Name, client, environment);
                    workspace.ConnectionError += message => Models.Notification.Send(null, options.Name + ": " + message, true);
                    Workspaces[options.Id] = workspace;
                    return workspace;
                }
                finally { if (diagnostic != null) manager.Diagnostic -= diagnostic; }
            }
            finally { Gate.Release(); }
        }
        private static async Task RebindAsync(RemoteWorkspace workspace, RemoteRepositoryClient client)
        {
            try { await workspace.RebindAsync(client).ConfigureAwait(false); }
            catch (Exception error) { Models.Notification.Send(null, workspace.Name + ": " + error.Message, true); }
        }
        public static async Task<RemoteWorkspace> RestoreAsync(string resource, CancellationToken cancellation = default)
        {
            var id = RepositoryPath.ConnectionId(resource);
            if (Workspaces.TryGetValue(id, out var workspace) && workspace.Connected) return workspace;
            var options = Load().FirstOrDefault(x => x.Id == id) ?? throw new IOException("Remote connection configuration is missing");
            return await ConnectAsync(options, cancellation: cancellation).ConfigureAwait(false);
        }
        public static async Task DisconnectAllAsync()
        {
            foreach (var manager in Managers.Values) await manager.DisposeAsync().ConfigureAwait(false);
            Managers.Clear(); Workspaces.Clear();
        }
    }
}
