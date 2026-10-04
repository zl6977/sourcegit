using System.Runtime.InteropServices;

namespace SourceGit.Core;

/// <summary>Resolves Linux symlinks before collapsing '..', preserving the host's path semantics.</summary>
public static class RemotePaths
{
    public static string Canonical(string path)
    {
        if (!OperatingSystem.IsLinux()) return Path.GetFullPath(path);
        var absolute = Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
        var resolved = RealPath(absolute, IntPtr.Zero);
        if (resolved != IntPtr.Zero)
        {
            try { return Marshal.PtrToStringUTF8(resolved)!; }
            finally { Free(resolved); }
        }
        var trimmed = absolute.TrimEnd('/');
        var parent = Path.GetDirectoryName(trimmed);
        if (parent == null || parent == trimmed) return Path.GetFullPath(absolute);
        return Path.Combine(Canonical(parent), Path.GetFileName(trimmed));
    }
    [DllImport("libc", EntryPoint = "realpath", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr RealPath(string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
