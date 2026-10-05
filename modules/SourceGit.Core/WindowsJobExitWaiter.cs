using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace SourceGit.Core;

// Job completion-port delivery is not guaranteed. Stable process waits are;
// a parent's exit prompts another authoritative snapshot including its offspring.
internal sealed class WindowsJobExitWaiter : IDisposable
{
    private readonly IntPtr _job;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed, _running;
    private readonly Dictionary<uint, Registration> _processes = new();
    private readonly Channel<bool> _exits = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Action<IReadOnlyCollection<uint>>? _reconciled;
    internal int SnapshotQueries { get; private set; }
    internal int MemberQueries { get; private set; }
    internal int? InitialSnapshotCapacity { get; set; }
    internal Func<IntPtr, IntPtr>? MembersQueryHandle { get; set; }
    internal Action<WaitHandle>? BeforeWaitRegistered { get; set; }
    internal WindowsJobExitWaiter(IntPtr job, Action<IReadOnlyCollection<uint>>? reconciled = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Job waits require Windows");
        if (job == IntPtr.Zero) throw new ArgumentException("Job handle required", nameof(job));
        _job = job; _reconciled = reconciled; _lifetimeToken = _lifetime.Token;
    }

    internal async Task WaitAsync(CancellationToken cancellation = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("A job waiter has one consumer");
            _running = true;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetimeToken);
        }
        using var waiting = linked;
        try
        {
            var changedRetries = 0;
            while (true)
            {
                waiting.Token.ThrowIfCancellationRequested();
                uint[] registeredIds;
                var changed = false;
                lock (_gate)
                {
                    waiting.Token.ThrowIfCancellationRequested();
                    // Reconcile only initially and after real process-exit signals.
                    var members = Members();
                    if (members.Length == 0) return;
                    var current = members.ToHashSet();
                    foreach (var previous in _processes.Keys.Where(id => !current.Contains(id)).ToArray())
                    { _processes[previous].Dispose(); _processes.Remove(previous); }
                    foreach (var id in members)
                    {
                        if (_processes.TryGetValue(id, out var previous))
                        {
                            if (!previous.Handle.WaitOne(0)) continue;
                            previous.Dispose(); _processes.Remove(id);
                        }
                        var process = OpenProcess(0x100000 | 0x1000, false, id); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
                        if (process == IntPtr.Zero)
                        {
                            var error = Marshal.GetLastPInvokeError();
                            if (error == 87 || error == 1168) { changed = true; continue; }
                            throw new Win32Exception(error, "Cannot register contained process exit");
                        }
                        var handle = new ProcessWaitHandle(process);
                        try
                        {
                            if (!IsProcessInJob(process, _job, out var contained))
                                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify process wait ownership");
                            // PID may have been reused between snapshot and OpenProcess.
                            if (!contained) { handle.Dispose(); changed = true; continue; }
                            BeforeWaitRegistered?.Invoke(handle);
                            var registered = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => _exits.Writer.TryWrite(true), null,
                                Timeout.Infinite, true);
                            _processes.Add(id, new Registration(handle, registered));
                        }
                        catch { handle.Dispose(); throw; }
                    }
                    registeredIds = _processes.Keys.ToArray();
                }
                _reconciled?.Invoke(registeredIds);
                if (changed)
                {
                    if (++changedRetries > 32) throw new IOException("Contained membership changed too often to register safe waits");
                    continue; // Known exit/PID replacement during collection, not a periodic scan.
                }
                changedRetries = 0;
                await _exits.Reader.ReadAsync(waiting.Token).ConfigureAwait(false);
            }
        }
        finally { Dispose(); }
    }

    private uint[] Members()
    {
        SnapshotQueries++;
        if (!QueryAccounting(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify contained tree cleanup");
        if (accounting.ActiveProcesses == 0) return [];
        var capacity = InitialSnapshotCapacity ?? Math.Max(16, (int)accounting.ActiveProcesses);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(InitialSnapshotCapacity));
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (capacity > 100000) throw new IOException("Contained process snapshot exceeds 100000 entries");
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts followed by
            // a variable ULONG_PTR array (8-byte preamble on both Win32/Win64).
            // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_process_id_list
            var bytes = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                MemberQueries++;
                if (!QueryMembers(MembersQueryHandle?.Invoke(_job) ?? _job, 3, buffer, bytes, out _))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 234) throw new Win32Exception(error, "Cannot snapshot contained processes");
                    capacity *= 2; continue; // ERROR_MORE_DATA: resize, never truncate membership.
                }
                var assigned = Marshal.ReadInt32(buffer);
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < assigned) { capacity = Math.Max(capacity * 2, assigned); continue; }
                if (count < 0 || count > capacity) throw new IOException("Invalid contained process snapshot");
                if (count == 0)
                {
                    // List/accounting changed during collection. Empty accounting
                    // is the terminal authority; inconsistency is a failed query.
                    if (QueryAccounting(_job, 1, out accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero) && accounting.ActiveProcesses == 0) return [];
                    throw new IOException("Contained process snapshot changed without an attachable member");
                }
                var ids = new uint[count];
                for (var index = 0; index < count; index++) ids[index] = checked((uint)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64());
                return ids;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Contained process snapshot changed too often to capture safely");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _exits.Writer.TryComplete();
            foreach (var process in _processes.Values) process.Dispose();
            _processes.Clear();
            _lifetime.Dispose();
        }
    }
    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
    }
    private sealed record Registration(WaitHandle Handle, RegisteredWaitHandle Wait) : IDisposable
    {
        public void Dispose() { Wait.Unregister(null); Handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryAccounting(IntPtr job, int info, out JobAccounting accounting, int bytes, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryMembers(IntPtr job, int info, IntPtr buffer, int bytes, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool contained);
}
