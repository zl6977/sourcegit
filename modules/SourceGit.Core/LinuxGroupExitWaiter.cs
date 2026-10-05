using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SourceGit.Core;

// Called only after .NET has reaped the managed root. Each stable pidfd
// belongs to one verified group member; waitid(P_PIDFD) never selects another job.
internal sealed class LinuxGroupExitWaiter
{
    private static readonly SemaphoreSlim WaitResources = new(64);
    private static readonly Lazy<bool> Supported = new(CheckSupport);
    private readonly int _group;
    private readonly Func<bool> _rootExited;
    private readonly Action<IReadOnlyCollection<int>>? _reconciled;
    private readonly Func<int, int>? _notificationDescriptor;
    private readonly int _memberLimit;
    private int _running;
    internal int SnapshotQueries { get; private set; }
    internal int Reaped { get; private set; }
    internal Func<int, int>? DescriptorProcessId { get; set; }
    internal Func<int, string, string>? VerifiedStartIdentity { get; set; }
    internal Func<int, int>? ReapDescriptor { get; set; }
    private sealed record Identity(int Pid, int Parent, int Group, int Session, string Start, string State);
    private sealed class Member(int descriptor, Identity identity)
    {
        internal readonly int Descriptor = descriptor;
        internal readonly Identity Identity = identity;
        internal bool ObserveExit = true;
    }

    internal LinuxGroupExitWaiter(int group, Func<bool> rootExited, Action<IReadOnlyCollection<int>>? reconciled = null, Func<int, int>? notificationDescriptor = null, int memberLimit = 4096)
    {
        if (group <= 0) throw new ArgumentOutOfRangeException(nameof(group));
        if (memberLimit <= 0) throw new ArgumentOutOfRangeException(nameof(memberLimit));
        _group = group; _rootExited = rootExited; _reconciled = reconciled;
        _notificationDescriptor = notificationDescriptor;
        _memberLimit = memberLimit;
    }

    internal async Task WaitAsync(CancellationToken cancellation = default)
    {
        EnsureSupported();
        if (!_rootExited()) throw new InvalidOperationException("The managed root must be reaped by .NET before descendant cleanup");
        cancellation.ThrowIfCancellationRequested();
        if (GroupGone()) return; // Ordinary commands need neither /proc enumeration nor a waiting thread.
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("Group waiter already running");
        try
        {
            await WaitResources.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (GroupGone()) return;
                // Bounded dedicated blocking resources; do not occupy thread-pool workers indefinitely.
                await Task.Factory.StartNew(() => Wait(cancellation), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
            finally { WaitResources.Release(); }
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    internal static void EnsureSupported()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Group exit notification requires Linux x64/arm64");
        _ = Supported.Value;
    }

    private void Wait(CancellationToken cancellation)
    {
        var pipe = new[] { -1, -1 };
        var members = new Dictionary<int, Member>();
        try
        {
            if (Pipe2(pipe, 0x80000 | 0x800) != 0) throw NativeError("Cannot create group-wait cancellation pipe");
            using var registration = cancellation.Register(() =>
            {
                if (Write(pipe[1], [1], 1) < 0 && Marshal.GetLastPInvokeError() != 11)
                    Trace.WriteLine("Group-wait cancellation pipe write failed");
            });
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                Reconcile();
                _reconciled?.Invoke(members.Keys.ToArray());
                if (members.Count == 0) return;
                var watched = members.Values.ToArray();
                var descriptors = new PollDescriptor[watched.Length + 1];
                descriptors[0] = new() { Descriptor = pipe[0], Events = 1 };
                for (var index = 0; index < watched.Length; index++)
                    descriptors[index + 1] = new() { Descriptor = _notificationDescriptor?.Invoke(watched[index].Descriptor) ?? watched[index].Descriptor, Events = watched[index].ObserveExit ? (short)1 : (short)0 };
                int result;
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    result = Poll(descriptors, (nuint)descriptors.Length, -1);
                } while (result < 0 && Marshal.GetLastPInvokeError() == 4);
                cancellation.ThrowIfCancellationRequested();
                if (result < 0) throw NativeError("Group exit notification failed");
                for (var index = 0; index < watched.Length; index++)
                {
                    var events = descriptors[index + 1].Returned;
                    if ((events & (8 | 32)) != 0) throw new IOException("Invalid group member notification descriptor");
                    if ((events & 16) != 0) Remove(watched[index].Identity.Pid);
                    else if ((events & 1) != 0)
                    {
                        if (TryReap(watched[index], cancellation)) Remove(watched[index].Identity.Pid);
                        else watched[index].ObserveExit = false; // Zombie with a live parent: wait for HUP or parent exit.
                    }
                }
            }

            void Reconcile()
            {
                // Immediate retries require an observed identity/exit race, not an elapsed interval.
                for (var race = 0; race < 32; race++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    SnapshotQueries++;
                    var snapshot = new Dictionary<int, Identity>();
                    var changed = false;
                    foreach (var directory in Directory.EnumerateDirectories("/proc"))
                    {
                        if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
                        Identity? identity;
                        try { identity = Read(pid); }
                        catch (UnauthorizedAccessException) { continue; }
                        if (identity?.Group == _group)
                        {
                            if (pid == _group) throw new IOException("Reaped root PID was reused; group identity cannot be accepted");
                            if (identity.Session != _group) throw new IOException("Group session identity changed");
                            // Reap already-adopted zombies one descriptor at a time before applying the live
                            // watch budget. A killed oversized tree must not remain permanently unreapable.
                            if (identity.Parent == Environment.ProcessId && identity.State is "Z" or "X")
                            {
                                var zombie = OpenDescriptor(pid);
                                if (zombie < 0)
                                {
                                    if (Marshal.GetLastPInvokeError() == 3) { changed = true; continue; }
                                    throw NativeError("Cannot register adopted zombie for reaping");
                                }
                                var reaped = false;
                                try
                                {
                                    var current = Read(pid);
                                    if (Matches(current, identity))
                                        reaped = TryReap(new(zombie, identity), cancellation);
                                }
                                finally { Close(zombie); }
                                if (reaped) { Remove(pid); changed = true; continue; }
                            }
                            snapshot.Add(pid, identity);
                            if (snapshot.Count > _memberLimit) throw new IOException("Group notification member limit exceeded");
                        }
                    }
                    foreach (var pair in members.ToArray())
                        if (!snapshot.TryGetValue(pair.Key, out var identity) || identity.Start != pair.Value.Identity.Start) Remove(pair.Key);
                    foreach (var identity in snapshot.Values)
                    {
                        if (identity.Parent == Environment.ProcessId || snapshot.ContainsKey(identity.Parent)) continue;
                        var current = Read(identity.Pid);
                        var parent = current == null ? null : Read(current.Parent);
                        if (current == null || current.Parent == Environment.ProcessId || parent == null || parent.Group == _group)
                        { changed = true; continue; }
                        throw new IOException("Group member parent escaped containment; descendant recovery ownership cannot be confirmed");
                    }
                    foreach (var identity in snapshot.Values)
                    {
                        if (!members.TryGetValue(identity.Pid, out var member))
                        {
                            var descriptor = OpenDescriptor(identity.Pid);
                            if (descriptor < 0)
                            {
                                var error = Marshal.GetLastPInvokeError();
                                if (error == 3) { changed = true; continue; }
                                if (error == 38) throw new PlatformNotSupportedException("Linux pidfd_open support is required");
                                throw new Win32Exception(error, "Cannot register group-member exit");
                            }
                            var verified = false;
                            try { verified = Matches(Read(identity.Pid), identity); }
                            finally { if (!verified) Close(descriptor); }
                            if (!verified) { changed = true; continue; }
                            member = new(descriptor, identity);
                            members.Add(identity.Pid, member);
                        }
                        if (identity.State is "Z" or "X")
                        {
                            if (TryReap(member, cancellation)) { Remove(identity.Pid); changed = true; }
                            else member.ObserveExit = false;
                        }
                    }
                    if (members.Count == 0)
                    {
                        if (Kill(-_group, 0) == 0) { changed = true; }
                        else
                        {
                            var error = Marshal.GetLastPInvokeError();
                            if (error != 3) throw new Win32Exception(error, "Cannot verify empty group");
                            return;
                        }
                    }
                    if (!changed) return;
                }
                throw new IOException("Group membership did not stabilize after observed exit/identity races");
            }
            void Remove(int pid) { if (members.Remove(pid, out var member)) Close(member.Descriptor); }
        }
        finally
        {
            foreach (var member in members.Values) Close(member.Descriptor);
            if (pipe[0] >= 0) Close(pipe[0]);
            if (pipe[1] >= 0) Close(pipe[1]);
        }
    }

    private bool TryReap(Member member, CancellationToken cancellation)
    {
        var info = new byte[128];
        while (WaitId(3, (uint)(ReapDescriptor?.Invoke(member.Descriptor) ?? member.Descriptor), info, 4 | 1) != 0) // P_PIDFD, WEXITED | WNOHANG; Linux 5.4+.
        {
            var error = Marshal.GetLastPInvokeError();
            cancellation.ThrowIfCancellationRequested();
            if (error == 4) continue;
            if (error == 10) return false; // ECHILD: still owned/reaped by its real parent or already reaped.
            if (error == 22) throw new PlatformNotSupportedException("Linux 5.4+ waitid(P_PIDFD) is required");
            throw new Win32Exception(error, "Cannot reap stable group member");
        }
        if (!HasExitedChild(info)) return false;
        Reaped++;
        return true;
    }

    // Supported 64-bit Linux siginfo_t aligns its union to eight bytes:
    // si_pid is at 16, si_uid at 20. UID zero is a valid root child.
    internal static bool HasExitedChild(ReadOnlySpan<byte> info) => BitConverter.ToInt32(info.Slice(16, 4)) != 0;

    private int OpenDescriptor(int pid) => (int)Syscall(434, DescriptorProcessId?.Invoke(pid) ?? pid, 0);
    private bool Matches(Identity? current, Identity expected) => current != null && current.Group == _group && current.Session == _group &&
        (VerifiedStartIdentity?.Invoke(current.Pid, current.Start) ?? current.Start) == expected.Start;

    private static Identity? Read(int pid)
    {
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var closing = stat.LastIndexOf(')');
        var fields = stat[(closing + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (closing < 0 || fields.Length <= 19) throw new IOException("Malformed group-member identity");
        return new(pid, int.Parse(fields[1]), int.Parse(fields[2]), int.Parse(fields[3]), fields[19], fields[0]);
    }
    private static Win32Exception NativeError(string message) => new(Marshal.GetLastPInvokeError(), message);
    private bool GroupGone()
    {
        if (Kill(-_group, 0) == 0) return false;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) return true;
        throw new Win32Exception(error, "Cannot verify process group identity");
    }
    private static bool CheckSupport()
    {
        var descriptor = (int)Syscall(434, Environment.ProcessId, 0);
        if (descriptor < 0) throw NativeError("Linux 5.4+ pidfd support is required for group cleanup");
        try
        {
            // Waiting on ourselves must be ECHILD on a kernel supporting P_PIDFD; never reap a managed child.
            if (WaitId(3, (uint)descriptor, new byte[128], 4 | 1) == -1 && Marshal.GetLastPInvokeError() == 10) return true;
            throw new PlatformNotSupportedException("Linux 5.4+ waitid(P_PIDFD) support is required for group cleanup");
        }
        finally { Close(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { internal int Descriptor; internal short Events, Returned; }
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long Syscall(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)] private static extern int Pipe2([Out] int[] descriptors, int flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)] private static extern int WaitId(int type, uint id, [Out] byte[] info, int options);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)] private static extern nint Write(int descriptor, byte[] value, nuint count);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
}
