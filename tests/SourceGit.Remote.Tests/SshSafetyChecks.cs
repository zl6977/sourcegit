using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class SshSafetyChecks
{
    internal static async Task RunAsync()
    {
        foreach (var message in new[] { "Permission denied (publickey,password)", "Too many authentication failures", "Host key verification failed", "REMOTE HOST IDENTIFICATION HAS CHANGED", "unknown remote failure", "Permission denied; connection reset by peer" })
            Check(!SshConnectionException.FromExit(255, message).Retryable, "Unsafe failure classified as retryable: " + message);
        Check(SshConnectionException.FromExit(255, "Connection refused").Retryable, "Network refusal not classified");
        Console.WriteLine("PASS Authentication, host-key and unknown SSH errors are terminal; clear network errors alone are retryable");

        var clock = new ManualTimeProvider();
        var policy = new SshConnectionPolicy(clock);
        using (await policy.EnterAsync(false, default)) policy.Failed(SshConnectionException.FromExit(255, "Permission denied"));
        await Error(policy.EnterAsync(false, default));
        var manual = policy.EnterAsync(true, default);
        Check(!manual.IsCompleted, "Manual retry bypassed minimum spacing");
        clock.Advance(TimeSpan.FromSeconds(29));
        Check(!manual.IsCompleted, "Second authentication attempt happened before 30 seconds");
        clock.Advance(TimeSpan.FromSeconds(6));
        using (await manual) policy.Failed(SshConnectionException.FromExit(255, "Connection refused"));
        var third = policy.EnterAsync(false, default);
        clock.Advance(TimeSpan.FromSeconds(54));
        Check(!third.IsCompleted, "Third attempt happened before 60 seconds");
        clock.Advance(TimeSpan.FromSeconds(11));
        using (await third) { }
        await Error(policy.EnterAsync(true, default));
        clock.Advance(TimeSpan.FromMinutes(10));
        using (await policy.EnterAsync(false, default)) { }
        Check(clock.EnabledTimers == 0, "Backoff leaked timers");
        Console.WriteLine("PASS Manual retry respects 30/60-second jittered spacing; all callers share a three-attempt rolling ten-minute limit");

        var cancelClock = new ManualTimeProvider();
        var cancelPolicy = new SshConnectionPolicy(cancelClock);
        using (await cancelPolicy.EnterAsync(false, default)) { }
        using var cancellation = new CancellationTokenSource();
        var pending = cancelPolicy.EnterAsync(false, cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new Exception("Canceled backoff admitted a connection"); } catch (OperationCanceledException) { }
        Check(cancelClock.EnabledTimers == 0, "Canceled backoff retained timers");
        Console.WriteLine("PASS Cancellation releases host admission and cancels backoff without spawning SSH");

        var host = "fixture-" + Guid.NewGuid().ToString("N") + ".invalid";
        await using var manager = new ConnectionManager(new ConnectionOptions("safety", "Safety fixture", "Ssh", host));
        var spawned = 0;
        manager.StartSshProcess = _ => { Interlocked.Increment(ref spawned); return Fake("", "Permission denied (publickey)", 255); };
        var callers = Enumerable.Range(0, 8).Select(_ => manager.ConnectAsync()).ToArray();
        foreach (var caller in callers) await Error(caller.WaitAsync(TimeSpan.FromSeconds(15)));
        Check(spawned == 1, "Concurrent callers spawned separate authentication attempts");
        await Error(manager.ConnectAsync());
        await using var other = new ConnectionManager(new ConnectionOptions("other", "Other fixture", "Ssh", host));
        other.StartSshProcess = _ => throw new Exception("A second profile bypassed the host failure latch");
        await Error(other.ConnectAsync());
        Check(spawned == 1, "Failed authentication was automatically attempted again");
        Console.WriteLine("PASS Eight actual manager callers share one fake SSH process; authentication failure blocks automatic opens and another profile on the same host");

        await using var held = new ConnectionManager(new ConnectionOptions("held", "Held fixture", "Ssh", Guid.NewGuid().ToString("N") + ".invalid"));
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        held.StartSshProcess = _ => { var child = Fake("", "", 0, hold: true); started.SetResult(child); return child; };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = held.ConnectAsync(firstCancellation.Token);
        var second = held.ConnectAsync(secondCancellation.Token);
        var process = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstCancellation.Cancel();
        try { await first; throw new Exception("Canceled waiter completed"); } catch (OperationCanceledException) { }
        Check(!second.IsCompleted && !process.HasExited, "One canceled waiter killed shared authentication");
        var exited = process.WaitForExitAsync();
        secondCancellation.Cancel();
        try { await second; throw new Exception("Last canceled waiter completed"); } catch (OperationCanceledException) { }
        await exited.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("PASS Canceling one waiter preserves shared authentication; canceling the last waiter terminates its fake SSH child");

        var root = Path.GetFullPath(Path.Combine("artifacts", "ssh-safety-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "fake.tar.gz");
        await File.WriteAllTextAsync(package, "fake upload consumed only by the test child");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(package)));
        await using var tunnel = new ConnectionManager(new ConnectionOptions("tunnel", "Tunnel fixture", "Ssh", Guid.NewGuid().ToString("N") + ".invalid", PackagePath: package, PackageSha256: digest));
        var calls = 0;
        tunnel.StartSshProcess = start =>
        {
            var index = Interlocked.Increment(ref calls);
            if (start.ArgumentList.Contains("-N")) return Fake("", "Host key verification failed", 255, noInput: true);
            if (index == 1) return Fake("Linux\nx86_64\n6.1.0\n/git\n/tar\n/sha256sum\n/flock\n/setsid\n/nohup\n", "", 0);
            if (index == 2) return Fake("", "", 0);
            var record = new ServerRecord(65000, "fixture", "fixture", 123);
            return Fake("SOURCEGIT_RECORD " + JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord) + "\nSOURCEGIT_PROCESS_START 1\n", "", 0);
        };
        var error = await Error(tunnel.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        Check(error is SshConnectionException { Retryable: false } && error.Message.Contains("Host key verification failed") && calls == 5, $"Tunnel failure lost diagnostics: calls={calls}, {error.GetType().Name}: {error.Message}");
        await Error(tunnel.ConnectAsync());
        Check(calls == 5, "Tunnel authentication failure restarted a complete connection");
        Console.WriteLine("PASS Fake bootstrap plus tunnel preserves host-key failure and prevents another full connection; no real ssh executable or remote host used");

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.StateChanged += state => { if (state == "Failed") failed.TrySetResult(); };
        Disconnect(manager);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(spawned == 1, "Recovery worker repeated failed authentication");
        Console.WriteLine("PASS Actual recovery worker stops on the shared authentication failure without spawning another process");

        await using var interactive = new ConnectionManager(new ConnectionOptions("interactive", "Interactive fixture", "Ssh", Guid.NewGuid().ToString("N") + ".invalid", InteractiveSsh: true));
        interactive.StartSshProcess = _ => throw new Exception("Automatic open prompted for interactive authentication");
        await Error(interactive.ConnectAsync());
        Console.WriteLine("PASS Interactive authentication requires a manual Connect action and never prompts on automatic history restore");

        await using var networkManager = new ConnectionManager(new ConnectionOptions("network", "Network fixture", "Ssh", Guid.NewGuid().ToString("N") + ".invalid"), TimeSpan.FromMilliseconds(150));
        var networkCalls = 0;
        networkManager.StartSshProcess = _ => { networkCalls++; return Fake("", "Connection refused", 255); };
        await Error(networkManager.ConnectAsync());
        var networkFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        networkManager.StateChanged += state => { if (state == "Failed") networkFailed.TrySetResult(); };
        Disconnect(networkManager);
        await networkFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(networkCalls == 1, "Recovery deadline spawned SSH during host backoff");
        Console.WriteLine("PASS Actual recovery deadline cancels network backoff without another authentication attempt");
    }

    private static Process Fake(string output, string error, int exit, bool hold = false, bool noInput = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(SshSafetyChecks).Assembly.Location, "--fake-ssh-safety-child", "--output", Convert.ToBase64String(Encoding.UTF8.GetBytes(output)), "--error", Convert.ToBase64String(Encoding.UTF8.GetBytes(error)), "--exit", exit.ToString() }) start.ArgumentList.Add(arg);
        if (hold) start.ArgumentList.Add("--hold");
        if (noInput) start.ArgumentList.Add("--no-input");
        return Process.Start(start)!;
    }
    private static async Task<IOException> Error(Task task)
    {
        try { await task; } catch (IOException error) { return error; }
        throw new Exception("Unsafe connection unexpectedly succeeded");
    }
    private static void Disconnect(ConnectionManager manager) =>
        typeof(ConnectionManager).GetMethod("OnDisconnected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(manager, [new IOException("injected disconnection")]);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
