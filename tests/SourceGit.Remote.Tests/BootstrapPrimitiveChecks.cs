using System.Diagnostics;
using SourceGit.Contracts;
using SourceGit.RemoteClient;
using SourceGit.Server;

internal static class BootstrapPrimitiveChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run bootstrap primitives on the Linux host");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using (var child = Start("sh", "-c", "read value"))
        {
            var identity = LinuxProcessExit.StartIdentity(child.Id);
            var wait = LinuxProcessExit.WaitAsync(child.Id, identity, token);
            Check(!wait.IsCompleted, "Living child completed exit notification");
            child.StandardInput.WriteLine("exit"); child.StandardInput.Close();
            await wait.WaitAsync(token);
            await child.WaitForExitAsync(token);
            Console.WriteLine("PASS pidfd registered before child exit completes from kernel notification");
            await LinuxProcessExit.WaitAsync(child.Id, identity, token);
            Console.WriteLine("PASS Exit before notification registration returns without periodic rediscovery");
        }
        using (var child = Start("sh", "-c", "read value"))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var identity = LinuxProcessExit.StartIdentity(child.Id);
            var wait = LinuxProcessExit.WaitAsync(child.Id, identity, cancel.Token);
            cancel.Cancel();
            try { await wait; throw new Exception("Cancellation did not end native wait"); }
            catch (OperationCanceledException) { }
            Check(!child.HasExited, "Cancellation killed independent process");
            await LinuxProcessExit.WaitAsync(child.Id, "different-start-identity", token);
            Check(!child.HasExited, "Identity mismatch killed/waited on another process");
            var descriptorsBefore = Directory.GetFiles("/proc/self/fd").Length;
            for (var i = 0; i < 20; i++)
            {
                using var iteration = new CancellationTokenSource();
                var pending = LinuxProcessExit.WaitAsync(child.Id, identity, iteration.Token);
                iteration.Cancel();
                try { await pending; throw new Exception("Repeated wait did not cancel"); } catch (OperationCanceledException) { }
            }
            Check(Directory.GetFiles("/proc/self/fd").Length <= descriptorsBefore + 2, "Canceled native waits leaked descriptors");
            child.StandardInput.WriteLine("exit"); child.StandardInput.Close();
            await child.WaitForExitAsync(token);
            Console.WriteLine("PASS Repeated canceled native waits reclaim descriptors without killing the process; stale identity cannot attach to its successor");
        }

        var root = Path.GetFullPath(Path.Combine("artifacts", "bootstrap-native", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var record = Path.Combine(root, "server.json");
        using var server = new RemoteServer(record);
        var running = server.RunAsync();
        var ready = await server.Ready.WaitAsync(token);
        using (var conflicting = Start("flock", "--nonblock", "--exclusive", record + ".lock", "true"))
        {
            conflicting.StandardInput.Close();
            await conflicting.WaitForExitAsync(token);
            Check(conflicting.ExitCode == 1, "Native flock did not see .NET FileShare.None lifetime lock");
        }
        using var blocking = Start("flock", "--exclusive", record + ".lock", "true");
        blocking.StandardInput.Close();
        await using var client = new RemoteRepositoryClient();
        await client.ConnectAsync("127.0.0.1", ready.Port, ready.Token, token);
        await client.ShutdownAsync(token);
        await running.WaitAsync(token);
        await blocking.WaitForExitAsync(token);
        Check(blocking.ExitCode == 0 && !File.Exists(record), "Blocking native lock did not follow record cleanup/release");
        Console.WriteLine("PASS Native flock interoperates with existing FileShare.None and wakes only after lifetime-lock release");
    }

    private static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Cannot start primitive fixture");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
