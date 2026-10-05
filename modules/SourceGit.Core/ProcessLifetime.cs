using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SourceGit.Core;

// Keep descendants addressable even after their original parent exits. In
// particular, a child holding stdout open must not evade a deadline's tree kill.
internal sealed class ProcessLifetime : IDisposable
{
    private readonly Process _process;
    private IntPtr _job;
    private static readonly Lazy<bool> Subreaper = new(() => Prctl(36, 1, 0, 0, 0) == 0);
    private ProcessLifetime(Process process) { _process = process; }
    internal WindowsJobExitWaiter CreateWindowsWaiter(Action<IReadOnlyCollection<uint>>? reconciled = null) => new(_job, reconciled);
    internal LinuxGroupExitWaiter CreateLinuxWaiter(Action<IReadOnlyCollection<int>>? reconciled = null, Func<int, int>? notificationDescriptor = null)
        => new(_process.Id, () => _process.HasExited, reconciled, notificationDescriptor);

    internal static ProcessStartInfo Prepare(ProcessStartInfo original)
    {
        if (!OperatingSystem.IsLinux()) return original;
        LinuxGroupExitWaiter.EnsureSupported(); // Reject unsupported kernels before starting any command or holding its tree.
        if (!Subreaper.Value) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enable subprocess reaping");
        var start = new ProcessStartInfo("setsid")
        {
            WorkingDirectory = original.WorkingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = original.StandardOutputEncoding, StandardErrorEncoding = original.StandardErrorEncoding,
        };
        start.Environment.Clear();
        foreach (var entry in original.Environment) start.Environment[entry.Key] = entry.Value;
        if (original.ArgumentList.Count > 0)
        {
            start.ArgumentList.Add("--wait"); start.ArgumentList.Add(original.FileName);
            foreach (var arg in original.ArgumentList) start.ArgumentList.Add(arg);
        }
        else start.Arguments = "--wait \"" + original.FileName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" " + original.Arguments;
        return start;
    }
    internal static ProcessLifetime Attach(Process process)
    {
        var lifetime = new ProcessLifetime(process);
        if (OperatingSystem.IsWindows())
        {
            lifetime._job = CreateJobObject(IntPtr.Zero, null);
            if (lifetime._job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create subprocess job");
            if (!AssignProcessToJobObject(lifetime._job, process.Handle))
            {
                var error = Marshal.GetLastPInvokeError(); lifetime.Dispose();
                throw new Win32Exception(error, "Cannot assign subprocess to its job");
            }
        }
        return lifetime;
    }
    internal void Terminate()
    {
        if (_job != IntPtr.Zero)
        {
            if (!TerminateJobObject(_job, 137)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot terminate subprocess job");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (Kill(-_process.Id, 9) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 3) throw new Win32Exception(error, "Cannot terminate subprocess group");
                // Cancellation may precede the wrapper's setsid call.
                if (!_process.HasExited) _process.Kill(true);
            }
        }
        else if (!_process.HasExited) _process.Kill(true);
    }
    internal bool IsEmpty
    {
        get
        {
            if (_job != IntPtr.Zero)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query subprocess cleanup");
                return accounting.ActiveProcesses == 0;
            }
            if (!OperatingSystem.IsLinux()) return _process.HasExited;
            // Startup can be canceled before setsid establishes the group;
            // an absent group alone cannot mean a still-live root is cleaned up.
            if (!_process.HasExited) return false;
            if (Kill(-_process.Id, 0) == 0) return false;
            var error = Marshal.GetLastPInvokeError();
            if (error == 3) return true;
            throw new Win32Exception(error, "Cannot verify subprocess group cleanup");
        }
    }
    internal async Task WaitForCleanupAsync()
    {
        if (_job != IntPtr.Zero)
        {
            using var waiter = CreateWindowsWaiter();
            await waiter.WaitAsync().ConfigureAwait(false);
            return;
        }
        if (OperatingSystem.IsLinux()) await CreateLinuxWaiter().WaitAsync().ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobAccounting information, int length, IntPtr returnedLength);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] private static extern int Prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
}
