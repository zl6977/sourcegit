using SourceGit.Server;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using SourceGit.Contracts;
using SourceGit.RemoteClient;

internal static class ServerDeadlineChecks
{
    internal static async Task RealAsync(string executable)
    {
        foreach (var persistentWrite in new[] { false, true })
        {
            var root = Path.GetFullPath(Path.Combine("artifacts", "server-deadlines", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "server.json");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { executable, "--record", path }) start.ArgumentList.Add(arg);
            start.Environment["SOURCEGIT_GRACE_SECONDS"] = "1";
            start.Environment["SOURCEGIT_CHANNEL_GRACE_SECONDS"] = "2";
            using var server = Process.Start(start)!;
            var stderr = server.StandardError.ReadToEndAsync();
            try
            {
                Check((await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!.StartsWith("SOURCEGIT_READY "), "Server did not become ready");
                var marker = Path.Combine(root, "completed.txt");
                if (persistentWrite)
                {
                    var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), RemoteJson.Default.ServerRecord)!;
                    await using var client = new RemoteRepositoryClient();
                    await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
                    await client.ExecuteGitAsync(new GitRequest("", ["init"], false, WorkingDirectory: root));
                    var repo = await client.OpenRepositoryAsync(root);
                    var command = OperatingSystem.IsWindows()
                        ? "powershell -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 4; Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value complete"))
                        : "sleep 4; printf complete > '" + marker.Replace("'", "'\"'\"'") + "'";
                    await client.CallAsync("ExecuteCustomCommand", new CustomCommandRequest(repo.Id, command, Guid.NewGuid().ToString("N")), RemoteJson.Default.CustomCommandRequest, RemoteJson.Default.OperationInfo);
                    await client.DisposeAsync();
                    // The configured one-second idle grace elapses while the
                    // persistent write still owns execution/cleanup.
                    await Task.Delay(TimeSpan.FromMilliseconds(1500));
                    Check(!server.HasExited && File.Exists(path), "Idle shutdown released an active persistent write");
                }
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                Check(server.ExitCode == 0 && !File.Exists(path), "Idle exit failed to stop cleanly or retained its own record: " + await stderr);
                if (persistentWrite) Check(File.Exists(marker), "Server exited before the persistent write completed");
                Console.WriteLine(persistentWrite
                    ? "PASS Real server keeps a disconnected persistent write alive beyond idle grace, then exits after completion"
                    : "PASS Real server with no owner exits at idle deadline and removes its owned record");
            }
            finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
        }
    }
    internal static void Run()
    {
        var clock = new ManualTimeProvider();
        var active = false;
        var stops = 0;
        using var scheduler = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => active, () => { if (active) return false; stops++; return true; }, clock);
        scheduler.Start();
        clock.Advance(TimeSpan.FromSeconds(29));
        Check(scheduler.Callbacks == 0 && stops == 0, "Idle policy woke before its deadline");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(scheduler.Callbacks == 1 && stops == 1 && clock.EnabledTimers == 0, "Idle deadline did not stop exactly once");
        Console.WriteLine("PASS Server idle shutdown uses one deadline callback without per-second scans");

        clock = new ManualTimeProvider();
        var store = new OperationStore();
        var cleanup = store.Register("owner:cleanup", "cleanup", "fixture", out _);
        var sibling = store.Register("owner:sibling", "sibling", "fixture", out _);
        stops = 0;
        using var busy = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => store.HasActive, () => { if (!store.TryBeginIdleShutdown()) return false; stops++; return true; }, clock);
        busy.Start();
        clock.Advance(TimeSpan.FromHours(1));
        Check(stops == 0 && busy.Callbacks == 0 && clock.EnabledTimers == 0, "Active/terminating operation caused periodic idle checks");
        cleanup.BeginTermination("injected cleanup pending");
        sibling.Complete(new("sibling", "succeeded"));
        busy.OperationChanged(); // Another operation completed; one still owns cleanup.
        clock.Advance(TimeSpan.FromHours(1));
        Check(stops == 0 && busy.Callbacks == 0, "A partial completion released idle shutdown");
        cleanup.Complete(new("cleanup", "failed", Error: "deadline"));
        busy.OperationChanged(); clock.Advance(TimeSpan.Zero);
        Check(stops == 1 && busy.Callbacks == 1, "Final completion failed to wake an overdue idle deadline");
        Console.WriteLine("PASS Active/terminating work parks idle scheduling; final completion wakes the overdue deadline");

        clock = new ManualTimeProvider();
        stops = 0;
        var closes = 0;
        using var channels = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => false, () => { stops++; return true; }, clock);
        channels.Start(); channels.Add("a", () => closes++);
        var stale = clock.CaptureScheduledCallbacks().Single();
        clock.Advance(TimeSpan.FromSeconds(50)); channels.Touch("a");
        stale(); clock.Advance(TimeSpan.FromSeconds(10));
        Check(closes == 0 && stops == 0, "Late callback ignored a channel heartbeat deadline");
        clock.Advance(TimeSpan.FromSeconds(50));
        Check(closes == 1 && stops == 0 && clock.EnabledTimers == 0, "Channel expiration was repeated or treated as actual disconnect");
        channels.Touch("a"); clock.Advance(TimeSpan.FromMinutes(2));
        Check(closes == 1 && stops == 0, "An expired channel was revived before disconnect confirmation");
        channels.Remove("a"); clock.Advance(TimeSpan.FromSeconds(29));
        Check(stops == 0, "Idle grace started before actual final disconnect");
        clock.Advance(TimeSpan.FromSeconds(1)); Check(stops == 1, "Final disconnect did not start idle grace");
        Console.WriteLine("PASS Heartbeats reset silence deadlines; expiration closes once and confirmed disconnect starts idle grace");

        clock = new ManualTimeProvider(); stops = 0; closes = 0;
        using var aliases = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => false, () => { stops++; return true; }, clock);
        aliases.Start(); aliases.Add("a", () => closes++); aliases.Add("b", () => closes++);
        aliases.Remove("a"); clock.Advance(TimeSpan.FromSeconds(30));
        Check(stops == 0, "Removing one alias started idle shutdown with a live sibling");
        var afterDispose = clock.CaptureScheduledCallbacks().Single();
        aliases.Dispose(); afterDispose(); clock.Advance(TimeSpan.FromHours(1));
        Check(stops == 0 && closes == 0 && clock.EnabledTimers == 0, "Disposed scheduler closed a channel or rearmed");
        Console.WriteLine("PASS Live sibling channels prevent idle exit; disposal stops late callbacks and rearming");

        clock = new ManualTimeProvider(); stops = 0;
        var racingStore = new OperationStore();
        OperationStore.Operation? registered = null;
        using var racing = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60),
            () => racingStore.HasActive, () =>
            {
                // Model registration between the snapshot and atomic store gate.
                if (registered == null) registered = racingStore.Register("owner:race", "race", "fixture", out _);
                if (!racingStore.TryBeginIdleShutdown()) return false;
                stops++; return true;
            }, clock);
        racing.Start(); clock.Advance(TimeSpan.FromSeconds(1));
        Check(stops == 0 && registered != null && clock.EnabledTimers == 0, "Idle snapshot bypassed atomic operation admission");
        clock.Advance(TimeSpan.FromHours(1));
        Check(racing.Callbacks == 1 && stops == 0, "Refused shutdown started periodic retries");
        registered!.Complete(new("race", "succeeded")); racing.OperationChanged(); clock.Advance(TimeSpan.Zero);
        Check(stops == 1 && racing.Callbacks == 2, "Final completion failed to retry previously refused idle admission");
        Console.WriteLine("PASS Atomic admission refuses idle shutdown if registration wins; completion resumes it without polling");

        clock = new ManualTimeProvider(); stops = 0; closes = 0;
        ServerDeadlines<string>? failedClose = null;
        using (failedClose = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => false, () => { stops++; return true; }, clock))
        {
            failedClose.Start();
            failedClose.Add("bad", () =>
            {
                if (++closes == 1) throw new IOException("injected channel close failure");
                failedClose.Remove("bad");
            });
            clock.Advance(TimeSpan.FromSeconds(60));
            clock.Advance(TimeSpan.FromMilliseconds(499));
            Check(closes == 1 && stops == 0, "Close failure became a busy loop or allowed idle shutdown");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Check(closes == 2 && stops == 0, "Close failure did not retry with its explicit error backoff");
            clock.Advance(TimeSpan.FromSeconds(30));
            Check(stops == 1 && clock.EnabledTimers == 0, "Recovered close continued retrying or failed idle shutdown");
        }
        Console.WriteLine("PASS Actual channel-close failure retries with bounded backoff and keeps ownership until disconnect");
        clock = new ManualTimeProvider();
        var diagnostics = new List<string>();
        var attempts = 0;
        ServerDeadlines<string>? diagnosticClose = null;
        using (diagnosticClose = new ServerDeadlines<string>(new object(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            () => false, () => true, clock))
        {
            diagnosticClose.Diagnostic = diagnostics.Add;
            diagnosticClose.Start();
            diagnosticClose.Add("private-channel-id", () =>
            {
                if (++attempts <= 8) throw new IOException("sensitive fixture token");
                diagnosticClose.Remove("private-channel-id");
            });
            clock.Advance(TimeSpan.FromSeconds(100));
            Check(attempts == 9 && diagnostics.Count == 5, "Close retries were lost or diagnostics were not coalesced");
            Check(diagnostics.Take(4).All(message => message.Contains("reason=IOException") && message.Contains("nextIn=")) &&
                diagnostics.Last().Contains("close-recovered attempts=8"), "Close retry/recovery omitted its cause/deadline/count");
            Check(diagnostics.All(message => !message.Contains("sensitive fixture token") && !message.Contains("private-channel-id") && message.Contains("elapsed=")),
                "Close diagnostics exposed sensitive text or omitted timing");
        }
        Console.WriteLine("PASS Channel close diagnostics coalesce repeated failures, omit sensitive details and report recovery counts/deadlines");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
