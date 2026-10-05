using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class WindowsDecisionLockChecks
{
    internal static async Task ChildAsync(string path, bool legacy)
    {
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, legacy ? FileShare.None : FileShare.ReadWrite);
        var overlap = new Overlap();
        if (!legacy && !LockFileEx(held.SafeFileHandle, 3, 0, 1, 0, ref overlap))
            throw new IOException("Native holder failed: " + Marshal.GetLastPInvokeError());
        Console.WriteLine("HELD");
        await Console.In.ReadLineAsync();
    }

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetTempPath(), "sourcegit-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacyPath = Path.Combine(root, "legacy.lock");
            using (var legacy = await Holder(legacyPath, true))
            {
                using var candidate = CreateFileW(legacyPath, 0xc0000000, 3, IntPtr.Zero, 4, 0x40000000, IntPtr.Zero);
                var error = Marshal.GetLastPInvokeError();
                Check(candidate.IsInvalid && error == 32, "Legacy share denial was not ERROR_SHARING_VIOLATION");
                await Release(legacy);
            }
            Console.WriteLine("PASS A real legacy FileShare.None holder rejects native overlapped opening before LockFileEx can subscribe");

            var nativePath = Path.Combine(root, "native.lock");
            using (var native = await Holder(nativePath, false))
            {
                try { using var old = new FileStream(nativePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); throw new Exception("Legacy caller bypassed native holder"); }
                catch (IOException error) { Check((error.HResult & 0xffff) == 32, "Legacy open failed for an unexpected reason"); }
                using var candidate = CreateFileW(nativePath, 0xc0000000, 3, IntPtr.Zero, 4, 0x40000000, IntPtr.Zero);
                Check(!candidate.IsInvalid, "Native waiter could not open shared file");
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = ThreadPool.RegisterWaitForSingleObject(ready, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, true);
                try
                {
                    Marshal.StructureToPtr(new Overlap { Event = ready.SafeWaitHandle.DangerousGetHandle() }, memory, false);
                    Check(!LockFileExPointer(candidate, 2, 0, 1, 0, memory) && Marshal.GetLastPInvokeError() == 997, "Native wait did not enter ERROR_IO_PENDING");
                    Check(!completion.Task.IsCompleted, "Held lock emitted premature completion");
                    await Release(native);
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Check(GetOverlappedResult(candidate, memory, out _, false), "Released lock did not complete successfully");
                }
                finally
                {
                    // Join the native request before releasing its OVERLAPPED/event storage.
                    CancelIoEx(candidate, memory);
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    registration.Unregister(null);
                    Marshal.FreeHGlobal(memory);
                }
            }
            Console.WriteLine("PASS Native byte-range waiter receives cross-process release notification; legacy open remains mutually exclusive");

            var cancelPath = Path.Combine(root, "cancel.lock");
            using (var native = await Holder(cancelPath, false))
            {
                using var candidate = CreateFileW(cancelPath, 0xc0000000, 3, IntPtr.Zero, 4, 0x40000000, IntPtr.Zero);
                Check(!candidate.IsInvalid, "Cancellation waiter open failed");
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
                var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = ThreadPool.RegisterWaitForSingleObject(ready, (_, _) => completion.TrySetResult(), null, Timeout.Infinite, true);
                try
                {
                    Marshal.StructureToPtr(new Overlap { Event = ready.SafeWaitHandle.DangerousGetHandle() }, memory, false);
                    Check(!LockFileExPointer(candidate, 2, 0, 1, 0, memory) && Marshal.GetLastPInvokeError() == 997, "Cancellation test did not hold a pending request");
                    Check(CancelIoEx(candidate, memory), "Native cancellation failed");
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var success = GetOverlappedResult(candidate, memory, out _, false);
                    var error = Marshal.GetLastPInvokeError();
                    Check(!success && error == 995 && !native.HasExited, "Cancellation bypassed the holder or lacked ERROR_OPERATION_ABORTED");
                }
                finally
                {
                    CancelIoEx(candidate, memory);
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    registration.Unregister(null);
                    Marshal.FreeHGlobal(memory);
                    await Release(native);
                }
            }
            Console.WriteLine("PASS CancelIoEx completes the pending native lock wait without releasing or terminating the other process holder");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<Process> Holder(string path, bool legacy)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--windows-decision-lock-child", path, legacy ? "legacy" : "native" }) start.ArgumentList.Add(argument);
        var child = Process.Start(start)!;
        if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "HELD") return child;
        child.StandardInput.Close();
        await child.WaitForExitAsync();
        var error = await child.StandardError.ReadToEndAsync(); child.Dispose();
        throw new IOException("Lock child failed: " + error);
    }
    private static async Task Release(Process child)
    {
        child.StandardInput.Close();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.ExitCode == 0, "Lock holder failed");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlap { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint low, uint high, ref Overlap overlap);
    [DllImport("kernel32.dll", EntryPoint = "LockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileExPointer(SafeFileHandle file, uint flags, uint reserved, uint low, uint high, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlap, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
