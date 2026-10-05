using System;
using System.Collections.Generic;

namespace SourceGit.Remote
{
    // Remote paths are opaque to the Windows filesystem and stay the canonical routing form. Display-only friendly forms (e.g. RemoteConnections.DisplayPath's \\wsl.localhost UNC) must never feed routing, I/O or persistence.
    public static class RepositoryPath
    {
        public const string Scheme = "sourcegit-remote://";
        public static bool IsRemote(string path) => path?.StartsWith(Scheme, StringComparison.Ordinal) == true;
        // Form in which repository paths are stored in nodes and workspace lists: local paths use slashes; remote URIs are kept verbatim.
        public static string StoredPath(string path) => IsRemote(path) ? path.TrimEnd('/') : path.Replace('\\', '/').TrimEnd('/');
        public static string ConnectionId(string path)
        {
            if (!IsRemote(path)) throw new ArgumentException("Expected a remote resource", nameof(path));
            var end = path.IndexOf('/', Scheme.Length);
            return end < 0 ? path[Scheme.Length..] : path[Scheme.Length..end];
        }
        public static string RemotePath(string path)
        {
            var end = path.IndexOf('/', Scheme.Length);
            return end < 0 ? "/" : path[end..];
        }
        public static string Remote(string connectionId, string path) => Normalize(Scheme + connectionId + "/" + path.TrimStart('/'));
        public static string Normalize(string path)
        {
            if (!IsRemote(path)) return System.IO.Path.GetFullPath(path);
            var parts = new List<string>();
            foreach (var segment in RemotePath(path).Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(segment);
            }
            return Scheme + ConnectionId(path) + "/" + string.Join('/', parts);
        }
        public static string Combine(params string[] paths)
        {
            if (paths.Length == 0) return string.Empty;
            if (!IsRemote(paths[0])) return System.IO.Path.Combine(paths);
            var value = paths[0];
            for (var i = 1; i < paths.Length; i++)
            {
                var part = paths[i];
                if (IsRemote(part)) value = part;
                else if (part.StartsWith('/')) value = Remote(ConnectionId(value), part);
                else value = value.TrimEnd('/') + "/" + part;
            }
            return Normalize(value);
        }
        public static string GetFullPath(string path) => Normalize(path);
        public static bool Exists(string path) => IsRemote(path) ? TryGetRemoteStatus(path, out var status) && status.Exists : System.IO.Path.Exists(path);
        internal static bool TryGetRemoteStatus(string path, out RemoteFileValue status)
        {
            status = null;
            try
            {
                if (!RemoteWorkspace.IsConnected(path)) return false;
                status = RemoteWorkspace.Stat(path);
                return true;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
            {
                // Match local Exists semantics: inaccessible/missing resources
                // and connection loss disable UI actions rather than throwing.
                return false;
            }
        }
        public static bool IsPathFullyQualified(string path) => IsRemote(path) || System.IO.Path.IsPathFullyQualified(path);
        // \\wsl.localhost shares report WSL-file ownership as foreign SIDs to the Windows git client, so git's
        // "dubious ownership" safety check rejects those repositories outright (fatal, blank tab in the GUI).
        // Only the WSL 9P mapping is known-broken; ordinary local paths keep the check (it guards against
        // running hooks from repositories you do not own).
        public static bool IsWslUnc(string path) =>
            OperatingSystem.IsWindows() &&
            (path?.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) == true ||
             path?.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) == true);
        public static string SafeOwnershipArg(string path) => IsWslUnc(path) ? "-c safe.directory=* " : string.Empty;
        public static bool IsPathRooted(string path) => IsRemote(path) || path?.StartsWith('/') == true || System.IO.Path.IsPathRooted(path);
        public static string GetDirectoryName(string path)
        {
            if (!IsRemote(path)) return System.IO.Path.GetDirectoryName(path);
            path = Normalize(path);
            var slash = path.LastIndexOf('/');
            return slash <= Scheme.Length + ConnectionId(path).Length ? Remote(ConnectionId(path), "/") : path[..slash];
        }
        public static string GetFileName(string path) => IsRemote(path) ? path.TrimEnd('/').Split('/')[^1] : System.IO.Path.GetFileName(path);
        public static string GetFileNameWithoutExtension(string path)
        {
            if (!IsRemote(path)) return System.IO.Path.GetFileNameWithoutExtension(path);
            var name = GetFileName(path);
            var dot = name.LastIndexOf('.');
            return dot < 0 ? name : name[..dot];
        }
        public static string GetExtension(string path)
        {
            if (!IsRemote(path)) return System.IO.Path.GetExtension(path);
            var name = GetFileName(path);
            var dot = name.LastIndexOf('.');
            return dot < 0 || dot == name.Length - 1 ? string.Empty : name[dot..];
        }
        public static string GetRelativePath(string root, string path)
        {
            if (!IsRemote(root)) return System.IO.Path.GetRelativePath(root, path);
            if (!IsRemote(path)) path = Remote(ConnectionId(root), path);
            if (ConnectionId(root) != ConnectionId(path)) return path;
            var a = RemotePath(Normalize(root)).Trim('/').Split('/');
            var b = RemotePath(Normalize(path)).Trim('/').Split('/');
            var common = 0;
            while (common < a.Length && common < b.Length && a[common] == b[common]) common++;
            var segments = new List<string>();
            for (var i = common; i < a.Length; i++) if (a[i].Length > 0) segments.Add("..");
            for (var i = common; i < b.Length; i++) if (b[i].Length > 0) segments.Add(b[i]);
            return segments.Count == 0 ? "." : string.Join('/', segments);
        }
    }
}
