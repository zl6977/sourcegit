using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SourceGit.Server;

// pidfd readiness is a kernel exit notification, not a periodic process query.
// A bootstrap owns at most one blocking wait and a cancellation pipe.
internal static class LinuxProcessExit
{
    internal static string StartIdentity(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var closing = stat.LastIndexOf(')');
        var fields = stat[(closing + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (closing < 0 || fields.Length <= 19) throw new IOException("Invalid process identity");
        return fields[19]; // starttime, field 22; comm can contain spaces/parentheses.
    }

    internal static async Task WaitAsync(int pid, string expectedStart, CancellationToken cancellation, Action? registered = null)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Remote exit notifications require Linux x64/arm64 with pidfd support");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStart);
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        cancellation.ThrowIfCancellationRequested();
        // SYS_pidfd_open is 434 on supported x64 and arm64 Linux ABIs.
        var descriptor = (int)Syscall(434, pid, 0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 3) return; // ESRCH: old process has already exited.
            if (error == 38) throw new PlatformNotSupportedException("Linux 5.3+ pidfd_open is required for remote exit notifications");
            throw new Win32Exception(error, "Cannot register remote process exit notification");
        }
        var pipe = new[] { -1, -1 };
        try
        {
            // Open the stable pidfd before checking identity. If the PID has been
            // reused, the original process is already gone; never wait on/kill its successor.
            try { if (StartIdentity(pid) != expectedStart) return; }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if (Pipe2(pipe, 0x80000 | 0x800) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create exit-wait cancellation pipe");
            using var registration = cancellation.Register(() =>
            {
                // Descriptor lifetime outlives registration; a second write isn't
                // needed and a full pipe already represents a cancellation wake-up.
                var wake = new byte[] { 1 };
                if (Write(pipe[1], wake, (nuint)wake.Length) < 0 && Marshal.GetLastPInvokeError() != 11)
                    Trace.WriteLine("Remote exit cancellation pipe write failed");
            });
            registered?.Invoke();
            await Task.Run(() =>
            {
                var descriptors = new[] { new PollDescriptor { Descriptor = descriptor, Events = 1 }, new PollDescriptor { Descriptor = pipe[0], Events = 1 } };
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = Poll(descriptors, (nuint)descriptors.Length, -1);
                    cancellation.ThrowIfCancellationRequested();
                    if (result < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == 4) continue; // EINTR: retry only an interrupted blocking syscall.
                        throw new Win32Exception(error, "Remote process exit notification failed");
                    }
                    if ((descriptors[0].Returned & (1 | 16)) != 0) return;
                    throw new IOException("Unexpected remote process notification events");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (pipe[0] >= 0) Close(pipe[0]);
            if (pipe[1] >= 0) Close(pipe[1]);
            Close(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { internal int Descriptor; internal short Events, Returned; }
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe2([Out] int[] descriptors, int flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int descriptor, byte[] value, nuint count);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
