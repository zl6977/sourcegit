using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SourceGit.Server;

internal static class BootstrapEndpointChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    internal static async Task BootstrapChildAsync(string record)
    {
        Environment.ExitCode = await Bootstrap.RunAsync(["--record", record], new Bootstrap.Hooks
        {
            EndpointRegistered = async endpoint =>
            {
                Console.WriteLine("ENDPOINT " + Path.GetDirectoryName(endpoint));
                await Console.In.ReadLineAsync();
                throw new OperationCanceledException("Endpoint fixture released before spawn");
            },
            StartChild = _ => throw new Exception("Endpoint fixture must not spawn a server"),
        });
    }
    internal static async Task ChildAsync(string record)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Endpoint fixture requires Linux");
        using var endpoint = new BootstrapEndpoint(record);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(endpoint.SocketPath)); socket.Listen(1);
        File.SetUnixFileMode(endpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine("ENDPOINT " + endpoint.DirectoryPath);
        await Console.In.ReadLineAsync();
    }

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Endpoint ownership checks require Linux");
        var root = Path.GetFullPath(Path.Combine("artifacts", "bootstrap-endpoint", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var record = Path.Combine(root, "server.json"); File.WriteAllText(record, "record-preserved");
        using (var child = Start(record))
        {
            var directory = await DirectoryFrom(child);
            try
            {
                Check(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) &&
                    File.GetUnixFileMode(Path.Combine(directory, "ready")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Endpoint permissions are not private");
                BootstrapEndpoint.Reconcile(record);
                Check(Directory.Exists(directory) && !child.HasExited, "Reconciliation removed a living bootstrap endpoint");
                try { using var competing = new FileStream(Path.Combine(directory, "owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); throw new Exception("Owner lock was not exclusive across processes"); }
                catch (IOException) { }
                BootstrapEndpoint.Reconcile(Path.Combine(root, "other-record.json"));
                Check(Directory.Exists(directory), "Another record claimed an active endpoint");
                Console.WriteLine("PASS Private endpoint binds launcher PID/start/nonce and holds an exclusive cross-process owner lock; reconciliation preserves active and other-record endpoints");
                child.Kill(); await child.WaitForExitAsync().WaitAsync(Limit);
                BootstrapEndpoint.Reconcile(record);
                Check(!Directory.Exists(directory) && File.ReadAllText(record) == "record-preserved", "Confirmed SIGKILL orphan was not cleaned or changed its server record");
                Console.WriteLine("PASS SIGKILL before child spawn leaves a provably owned endpoint; next startup reclaims only its socket/lock/directory and preserves the server record");
            }
            finally { if (!child.HasExited) { child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(Limit); } BootstrapEndpoint.Reconcile(record); }
        }

        using (var child = Start(record))
        {
            var directory = await DirectoryFrom(child);
            child.Kill(); await child.WaitForExitAsync().WaitAsync(Limit);
            var owner = Path.Combine(directory, "owner");
            var protectedFile = Path.Combine(root, "unrelated-file"); File.WriteAllText(protectedFile, "untouched");
            File.Delete(owner); File.CreateSymbolicLink(owner, protectedFile);
            BootstrapEndpoint.Reconcile(record);
            Check(Directory.Exists(directory) && File.ReadAllText(protectedFile) == "untouched", "Symlink ownership was accepted or followed");
            File.Delete(owner); File.WriteAllText(owner, ""); File.SetUnixFileMode(owner, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(directory, "unrelated-entry"), "keep");
            BootstrapEndpoint.Reconcile(record);
            Check(Directory.Exists(directory) && File.Exists(Path.Combine(directory, "unrelated-entry")), "Unknown contents were recursively removed");
            File.Delete(Path.Combine(directory, "unrelated-entry")); BootstrapEndpoint.Reconcile(record);
            Check(!Directory.Exists(directory), "Restored owned orphan could not be reconciled");
            Console.WriteLine("PASS Symlink owner files and unexpected entries prevent orphan deletion; unrelated file contents remain intact");
        }

        // An old PID/start identity in an empty directory covers SIGKILL during the mkdir/owner gap.
        var prefix = Bootstrap.EndpointPrefix(record);
        string deadIdentity;
        int deadPid;
        using (var child = Start(record))
        {
            var created = await DirectoryFrom(child);
            deadPid = child.Id; deadIdentity = LinuxProcessExit.StartIdentity(deadPid);
            child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(Limit);
            Check(!Directory.Exists(created), "Normal owner disposal left its endpoint");
        }
        var empty = Path.Combine(Path.GetTempPath(), prefix + deadPid + "-" + deadIdentity + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var legacy = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var live = Path.Combine(Path.GetTempPath(), prefix + Environment.ProcessId + "-" + LinuxProcessExit.StartIdentity(Environment.ProcessId) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(live, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            BootstrapEndpoint.Reconcile(record);
            Check(!Directory.Exists(empty) && Directory.Exists(legacy) && Directory.Exists(live), "Empty-gap cleanup accepted missing/active identity");
            Console.WriteLine("PASS Startup reconciles an empty mkdir/owner crash gap, preserves legacy unknown ownership and a living owner even before lock publication");
        }
        finally { Directory.Delete(legacy); Directory.Delete(live); if (Directory.Exists(empty)) Directory.Delete(empty); }

        var actualRecord = Path.Combine(root, "actual-bootstrap.json");
        using (var child = Start(actualRecord, true))
        {
            var directory = await DirectoryFrom(child);
            try
            {
                BootstrapEndpoint.Reconcile(actualRecord);
                Check(Directory.Exists(directory) && !File.Exists(actualRecord), "Actual bootstrap pre-spawn ownership was lost");
                child.Kill(); await child.WaitForExitAsync().WaitAsync(Limit);
                BootstrapEndpoint.Reconcile(actualRecord);
                Check(!Directory.Exists(directory) && !File.Exists(actualRecord), "Actual bootstrap SIGKILL left an endpoint or published a server");
                Console.WriteLine("PASS Actual Bootstrap.RunAsync SIGKILL after listener registration and before child spawn is reconciled on next startup without creating a server record");
            }
            finally
            {
                if (!child.HasExited) { child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(Limit); }
                BootstrapEndpoint.Reconcile(actualRecord);
            }
        }
    }

    internal static async Task ForeignOwnerAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("UID ownership checks require Linux");
        var root = Path.GetFullPath(Path.Combine("artifacts", "bootstrap-endpoint-uid", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var record = Path.Combine(root, "server.json");
        using var child = Start(record);
        var directory = await DirectoryFrom(child);
        try
        {
            child.Kill(); await child.WaitForExitAsync().WaitAsync(Limit);
            var uid = GetEuid();
            try
            {
                await ChangeUser(directory, uid == 0 ? 1000u : 0u);
                Check(!BootstrapEndpoint.TryCleanStale(directory, record) && Directory.Exists(directory), "A different Unix user's endpoint was accepted as owned");
            }
            finally { await ChangeUser(directory, uid); }
            Check(BootstrapEndpoint.TryCleanStale(directory, record) && !Directory.Exists(directory), "Restored current-user endpoint could not be reclaimed");
            Console.WriteLine("PASS Actual foreign Unix UID prevents orphan reconciliation; restored ownership permits cleanup without touching server records");
        }
        finally
        {
            if (!child.HasExited) { child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(Limit); }
            BootstrapEndpoint.Reconcile(record);
        }
    }

    private static async Task ChangeUser(string directory, uint uid)
    {
        var info = new ProcessStartInfo("sudo") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-n", "chown", uid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--", directory }) info.ArgumentList.Add(argument);
        using var change = Process.Start(info)!;
        var error = change.StandardError.ReadToEndAsync();
        await change.WaitForExitAsync().WaitAsync(Limit);
        Check(change.ExitCode == 0, "Isolated UID fixture requires noninteractive chown: " + await error);
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();

    private static Process Start(string record, bool bootstrap = false)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(info.FileName) == "dotnet") info.ArgumentList.Add(typeof(BootstrapEndpointChecks).Assembly.Location);
        info.ArgumentList.Add(bootstrap ? "--bootstrap-endpoint-parent" : "--bootstrap-endpoint-child"); info.ArgumentList.Add(record);
        return Process.Start(info)!;
    }
    private static async Task<string> DirectoryFrom(Process child)
    {
        var line = await child.StandardOutput.ReadLineAsync().WaitAsync(Limit);
        if (line?.StartsWith("ENDPOINT ", StringComparison.Ordinal) != true) throw new IOException("Endpoint child failed: " + await child.StandardError.ReadToEndAsync().WaitAsync(Limit));
        return line[9..];
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
