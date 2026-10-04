using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Remote
{
    public static class RepositoryFile
    {
        public static string GetTempFileName(string repository)
        {
            if (!RepositoryPath.IsRemote(repository)) return Path.GetTempFileName();
            var workspace = RemoteWorkspace.ForPath(repository);
            var result = workspace.Client.FileAsync(new SourceGit.Contracts.FileRequest("", "tempfile", workspace.Environment.Home)).GetAwaiter().GetResult();
            return workspace.Resource(result.Entries[0].Path);
        }
        public static bool Exists(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return File.Exists(path);
            if (!RemoteWorkspace.IsConnected(path)) return false;
            var stat = RemoteWorkspace.Stat(path);
            return stat.Exists && !stat.Directory;
        }
        public static string ReadAllText(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return File.ReadAllText(path);
            using var stream = OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        public static async Task<string> ReadAllTextAsync(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return await File.ReadAllTextAsync(path).ConfigureAwait(false);
            using var stream = OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        public static void WriteAllText(string path, string content) => WriteAllTextAsync(path, content).GetAwaiter().GetResult();
        public static async Task WriteAllTextAsync(string path, string content)
        {
            if (!RepositoryPath.IsRemote(path)) { await File.WriteAllTextAsync(path, content).ConfigureAwait(false); return; }
            await using var stream = Create(path);
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }
        public static Task WriteAllLinesAsync(string path, IEnumerable<string> lines) => WriteAllTextAsync(path, string.Join(RepositoryPath.IsRemote(path) ? "\n" : Environment.NewLine, lines) + (RepositoryPath.IsRemote(path) ? "\n" : Environment.NewLine));
        public static async Task AppendAllLinesAsync(string path, IEnumerable<string> lines)
        {
            if (!RepositoryPath.IsRemote(path)) { await File.AppendAllLinesAsync(path, lines).ConfigureAwait(false); return; }
            await using var stream = Open(path, FileMode.Append, FileAccess.Write);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).ConfigureAwait(false);
        }
        public static Stream OpenRead(string path) => Open(path, FileMode.Open, FileAccess.Read);
        public static Stream Create(string path) => Open(path, FileMode.Create, FileAccess.ReadWrite);
        public static Stream Open(string path, FileMode mode, FileAccess access = FileAccess.ReadWrite, FileShare share = FileShare.None)
        {
            if (!RepositoryPath.IsRemote(path)) return File.Open(path, mode, access, share);
            return new RepositoryStream(path, mode, access);
        }
        public static void Delete(string path)
        {
            if (RepositoryPath.IsRemote(path)) RemoteWorkspace.FileAsync(path, "delete").GetAwaiter().GetResult();
            else File.Delete(path);
        }
        public static void Move(string source, string destination, bool overwrite = false)
        {
            if (!RepositoryPath.IsRemote(source) && !RepositoryPath.IsRemote(destination)) { File.Move(source, destination, overwrite); return; }
            if (RepositoryPath.IsRemote(source) && RepositoryPath.IsRemote(destination) && RepositoryPath.ConnectionId(source) == RepositoryPath.ConnectionId(destination))
                RemoteWorkspace.FileAsync(source, "move", destination: destination, overwrite: overwrite).GetAwaiter().GetResult();
            else { Copy(source, destination, overwrite); Delete(source); }
        }
        public static void Copy(string source, string destination, bool overwrite = false)
        {
            if (!RepositoryPath.IsRemote(source) && !RepositoryPath.IsRemote(destination)) { File.Copy(source, destination, overwrite); return; }
            using var reader = OpenRead(source);
            using var writer = Open(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
            reader.CopyTo(writer);
        }
        public static UnixFileMode GetUnixFileMode(string path)
        {
            if (RepositoryPath.IsRemote(path)) throw new NotSupportedException("Remote permissions must be changed by the server");
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return File.GetUnixFileMode(path);
        }
        public static void SetUnixFileMode(string path, UnixFileMode mode)
        {
            if (RepositoryPath.IsRemote(path)) throw new NotSupportedException("Remote permissions must be changed by the server");
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(path, mode);
        }
    }
    public static class RepositoryDirectory
    {
        public static bool Exists(string path)
        {
            if (!RepositoryPath.IsRemote(path)) return Directory.Exists(path);
            // A disconnected (or never-connected) workspace must read as absent, never throw: UI paths
            // (context menus, node gates) call this synchronously and an unhandled throw crashes the app.
            if (!RemoteWorkspace.IsConnected(path)) return false;
            var stat = RemoteWorkspace.Stat(path);
            return stat.Exists && stat.Directory;
        }
        public static RepositoryDirectoryInfo CreateDirectory(string path)
        {
            if (!RepositoryPath.IsRemote(path)) Directory.CreateDirectory(path);
            else RemoteWorkspace.FileAsync(path, "mkdir").GetAwaiter().GetResult();
            return new(path);
        }
        public static void Delete(string path, bool recursive = false)
        {
            if (!RepositoryPath.IsRemote(path)) Directory.Delete(path, recursive);
            else RemoteWorkspace.FileAsync(path, recursive ? "deleteTree" : "deleteDirectory").GetAwaiter().GetResult();
        }
        public static void Move(string source, string destination)
        {
            if (!RepositoryPath.IsRemote(source) && !RepositoryPath.IsRemote(destination)) Directory.Move(source, destination);
            else if (RepositoryPath.ConnectionId(source) == RepositoryPath.ConnectionId(destination)) RemoteWorkspace.FileAsync(source, "move", destination: destination).GetAwaiter().GetResult();
            else throw new NotSupportedException("Moving a directory between hosts requires explicit export/import");
        }
    }
    public sealed class RepositoryFileInfo
    {
        public RepositoryFileInfo(string path) => FullName = RepositoryPath.GetFullPath(path);
        public string FullName { get; }
        public string Name => RepositoryPath.GetFileName(FullName);
        public string Extension => RepositoryPath.GetExtension(FullName);
        public bool Exists => RepositoryFile.Exists(FullName);
        public long Length => RepositoryPath.IsRemote(FullName) ? RemoteWorkspace.Stat(FullName).Length : new FileInfo(FullName).Length;
        public DateTime LastWriteTimeUtc => RepositoryPath.IsRemote(FullName) ? RemoteWorkspace.Stat(FullName).LastWriteTimeUtc : new FileInfo(FullName).LastWriteTimeUtc;
        public DateTime LastWriteTime => LastWriteTimeUtc.ToLocalTime();
        public string DirectoryName => RepositoryPath.GetDirectoryName(FullName);
        public RepositoryDirectoryInfo Directory => new(DirectoryName);
    }
    public sealed class RepositoryDirectoryInfo
    {
        public RepositoryDirectoryInfo(string path) => FullName = RepositoryPath.GetFullPath(path);
        public string FullName { get; }
        public string Name => RepositoryPath.GetFileName(FullName);
        public bool Exists => RepositoryDirectory.Exists(FullName);
        public RepositoryDirectoryInfo Parent => RepositoryPath.GetDirectoryName(FullName) is { } parent && parent != FullName ? new(parent) : null;
        public RepositoryFileInfo[] GetFiles(string pattern = "*") => GetFiles(pattern, new EnumerationOptions());
        public RepositoryFileInfo[] GetFiles(string pattern, EnumerationOptions options)
        {
            if (!RepositoryPath.IsRemote(FullName)) return new DirectoryInfo(FullName).GetFiles(pattern, options).Select(x => new RepositoryFileInfo(x.FullName)).ToArray();
            return RemoteWorkspace.List(FullName).Where(x => !x.Directory && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, x.Name)).Select(x => new RepositoryFileInfo(RepositoryPath.Combine(FullName, x.Name))).ToArray();
        }
        public RepositoryDirectoryInfo[] GetDirectories(string pattern = "*") => GetDirectories(pattern, new EnumerationOptions());
        public RepositoryDirectoryInfo[] GetDirectories(string pattern, EnumerationOptions options)
        {
            if (!RepositoryPath.IsRemote(FullName)) return new DirectoryInfo(FullName).GetDirectories(pattern, options).Select(x => new RepositoryDirectoryInfo(x.FullName)).ToArray();
            return RemoteWorkspace.List(FullName).Where(x => x.Directory && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, x.Name)).Select(x => new RepositoryDirectoryInfo(RepositoryPath.Combine(FullName, x.Name))).ToArray();
        }
    }
    internal sealed class RepositoryStream : Stream
    {
        private readonly string _path;
        private readonly FileAccess _access;
        private long _position;
        private long _length;
        public RepositoryStream(string path, FileMode mode, FileAccess access)
        {
            _path = path; _access = access;
            var stat = RemoteWorkspace.Stat(path);
            if (mode == FileMode.Open && !stat.Exists) throw new FileNotFoundException("Remote file does not exist", path);
            if (mode == FileMode.CreateNew && stat.Exists) throw new IOException("Remote file already exists");
            _length = stat.Length;
            if (mode is FileMode.Create or FileMode.CreateNew || mode is FileMode.OpenOrCreate or FileMode.Append && !stat.Exists)
            {
                RemoteWorkspace.FileAsync(path, "write", content: [], truncate: true, createNew: mode == FileMode.CreateNew).GetAwaiter().GetResult();
                _length = 0;
            }
            if (mode == FileMode.Append) _position = _length;
        }
        public override bool CanRead => _access is FileAccess.Read or FileAccess.ReadWrite;
        public override bool CanWrite => _access is FileAccess.Write or FileAccess.ReadWrite;
        public override bool CanSeek => true;
        public override long Length => _length;
        public override long Position { get => _position; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellation = default)
        {
            if (!CanRead) throw new NotSupportedException();
            if (_position >= _length) return 0;
            var result = await RemoteWorkspace.FileAsync(_path, "read", offset: _position, count: Math.Min(buffer.Length, 64 * 1024), cancellation: cancellation).ConfigureAwait(false);
            result.Content.AsMemory().CopyTo(buffer);
            _position += result.Content.Length;
            return result.Content.Length;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellation = default)
        {
            if (!CanWrite) throw new NotSupportedException();
            while (buffer.Length > 0)
            {
                var part = buffer[..Math.Min(buffer.Length, 64 * 1024)];
                await RemoteWorkspace.FileAsync(_path, "write", content: part.ToArray(), offset: _position, cancellation: cancellation).ConfigureAwait(false);
                _position += part.Length;
                _length = Math.Max(_length, _position);
                buffer = buffer[part.Length..];
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentException("Invalid origin") }; return Position; }
        public override void Flush() { }
        public override void SetLength(long value)
        {
            if (!CanWrite || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            RemoteWorkspace.FileAsync(_path, "truncate", offset: value).GetAwaiter().GetResult();
            _length = value;
        }
    }
}
