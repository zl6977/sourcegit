using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SourceGit.Server;

// An endpoint's name binds its record, launcher PID/start identity and nonce. The owner lock
// lives until bootstrap exits. Reconciliation is startup-triggered, never a recurring scan.
internal sealed class BootstrapEndpoint : IDisposable
{
    internal string DirectoryPath { get; }
    internal string SocketPath => Path.Combine(DirectoryPath, "ready");
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    private readonly FileStream _owner;
    private int _disposed;

    internal BootstrapEndpoint(string record)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Bootstrap endpoints require Linux");
        DirectoryPath = Path.Combine(Path.GetTempPath(), Bootstrap.EndpointPrefix(record) + Environment.ProcessId + "-" + LinuxProcessExit.StartIdentity(Environment.ProcessId) + "-" + Nonce);
        if (Mkdir(DirectoryPath, 0x1c0) != 0) throw NativeError("Cannot create private ready endpoint"); // Atomic mkdir 0700, refuses an existing path.
        try
        {
            if (!Owned(DirectoryPath, 0x4000, 0x1c0)) throw new IOException("Cannot confirm private ready-endpoint ownership");
            _owner = new FileStream(Path.Combine(DirectoryPath, "owner"), new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch { Directory.Delete(DirectoryPath); throw; }
    }

    internal static void Reconcile(string record, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Bootstrap endpoints require Linux");
        var prefix = Bootstrap.EndpointPrefix(record);
        var failures = 0; string? firstError = null;
        foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), prefix + "*"))
        {
            cancellation.ThrowIfCancellationRequested();
            try { TryCleanStale(directory, record); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { failures++; firstError ??= error.GetType().Name; }
        }
        if (failures != 0) Console.Error.WriteLine("SOURCEGIT_BOOTSTRAP_ENDPOINT_RECONCILE " + failures + " failures; " + firstError);
    }

    internal static bool TryCleanStale(string directory, string record)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var prefix = Bootstrap.EndpointPrefix(record);
        var name = Path.GetFileName(directory);
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd('/') ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Owned(directory, 0x4000, 0x1c0)) return false;
        var identity = name[prefix.Length..].Split('-');
        if (identity.Length != 3 || !int.TryParse(identity[0], out var pid) || pid <= 0 ||
            !long.TryParse(identity[1], out var start) || start <= 0 || !Guid.TryParseExact(identity[2], "N", out _)) return false;
        if (Alive(pid, identity[1])) return false;
        var owner = Path.Combine(directory, "owner");
        if (!File.Exists(owner))
        {
            // SIGKILL between mkdir and owner-file creation: only an empty owned directory is reclaimable.
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return false;
            Directory.Delete(directory); return true;
        }
        if (!Owned(owner, 0x8000, 0x180)) return false;
        using (var ownership = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (Alive(pid, identity[1]) || !DeleteReady(directory)) return false;
        }
        File.Delete(owner);
        Directory.Delete(directory);
        return true;
    }

    private static bool Alive(int pid, string expected)
    {
        try { return LinuxProcessExit.StartIdentity(pid) == expected; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool DeleteReady(string directory)
    {
        if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) is not ("owner" or "ready"))) return false;
        var ready = Path.Combine(directory, "ready");
        // Never follow a ready symlink or remove an arbitrary regular file.
        if (Stat(ready) is { } state)
        {
            if (!IsOwned(state, 0xc000, 0x180)) return false;
            File.Delete(ready);
        }
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Owned(DirectoryPath, 0x4000, 0x1c0) && DeleteReady(DirectoryPath))
            {
                File.Delete(Path.Combine(DirectoryPath, "owner"));
                Directory.Delete(DirectoryPath);
            }
        }
        catch (IOException error) { Console.Error.WriteLine("SOURCEGIT_BOOTSTRAP_ENDPOINT " + error.GetType().Name); }
        finally { _owner.Dispose(); }
    }

    private static bool Owned(string path, ushort type, ushort permissions) => Stat(path) is { } state && IsOwned(state, type, permissions);
    private static bool IsOwned(StatResult state, ushort type, ushort permissions) =>
        (state.Mask & 11) == 11 && state.User == GetEuid() && (state.Mode & 0xf000) == type && (state.Mode & 0xfff) == permissions;
    private static StatResult? Stat(string path)
    {
        // statx has a fixed Linux UAPI layout on both supported ABIs; no-follow preserves symlink boundaries.
        if (Statx(-100, path, 0x100, 11, out var state) == 0) return state;
        if (Marshal.GetLastPInvokeError() == 2) return null;
        throw NativeError("Cannot verify ready-endpoint ownership");
    }
    private static Win32Exception NativeError(string message) => new(Marshal.GetLastPInvokeError(), message);
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct StatResult
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint User;
        [FieldOffset(28)] internal ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int directory, string path, int flags, uint mask, out StatResult state);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)] private static extern int Mkdir(string path, uint mode);
}
