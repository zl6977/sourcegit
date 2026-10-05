using System.Diagnostics;
using SourceGit.Core;

internal static class LinuxGroupPressureChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);
    internal static async Task RunAsync(string fixture)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run native member pressure on Linux");
        Console.WriteLine(File.ReadLines("/proc/self/limits").Single(line => line.StartsWith("Max open files")));
        foreach (var count in new[] { 4096, 4097 })
        {
            var start = new ProcessStartInfo(fixture)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(count.ToString());
            using var root = Process.Start(ProcessLifetime.Prepare(start))!;
            using var lifetime = ProcessLifetime.Attach(root);
            var stderr = root.StandardError.ReadToEndAsync();
            try
            {
                Check(await root.StandardOutput.ReadLineAsync().WaitAsync(Limit) == "ROOT_READY", "Pressure root not ready");
                root.StandardInput.WriteLine("spawn"); root.StandardInput.Flush();
                var ready = await root.StandardOutput.ReadLineAsync().WaitAsync(Limit);
                Check(ready == "CHILDREN:" + count, "Fixture did not create every requested live child: " + ready);
                root.StandardInput.WriteLine("exit"); root.StandardInput.Close();
                await root.WaitForExitAsync().WaitAsync(Limit);
                Check(root.ExitCode == 37 && !lifetime.IsEmpty, "Root exit discarded live descendants");
                var baseline = Fds();
                var peak = baseline;
                if (count == 4096)
                {
                    using var cancel = new CancellationTokenSource();
                    var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var waiter = lifetime.CreateLinuxWaiter(ids =>
                    {
                        if (ids.Count != count) return;
                        peak = Fds(); registered.TrySetResult();
                    });
                    var waiting = waiter.WaitAsync(cancel.Token);
                    try
                    {
                        await registered.Task.WaitAsync(Limit);
                        var snapshots = waiter.SnapshotQueries;
                        await Task.Delay(250);
                        Check(!waiting.IsCompleted && waiter.SnapshotQueries == snapshots && peak >= baseline + count && peak <= baseline + count + 8,
                            "Full live watch set missed members, rescanned idle state or exceeded descriptor budget");
                    }
                    finally
                    {
                        cancel.Cancel();
                        try { await waiting.WaitAsync(Limit); throw new Exception("Live pressure wait completed as success"); }
                        catch (OperationCanceledException) { }
                    }
                    Check(Fds() <= baseline + 8 && !lifetime.IsEmpty, "Pressure cancellation leaked descriptors or released ownership");
                    Console.WriteLine($"PASS 4096 real live members use stable pidfds without idle scans; cancellation reclaims descriptors (baseline={baseline}, peak={peak}, final={Fds()})");
                }
                else
                {
                    var waiter = lifetime.CreateLinuxWaiter();
                    try { await waiter.WaitAsync().WaitAsync(Limit); throw new Exception("4097 live members bypassed the production limit"); }
                    catch (IOException error) { Check(error.Message.Contains("member limit"), "Wrong live pressure rejection"); }
                    Check(!lifetime.IsEmpty && Fds() <= baseline + 8, "Over-limit rejection abandoned containment or descriptors");
                    Console.WriteLine("PASS 4097 actual live members are explicitly rejected while cleanup responsibility remains owned");
                }
                lifetime.Terminate();
                var cleanup = lifetime.CreateLinuxWaiter();
                await cleanup.WaitAsync().WaitAsync(Limit);
                Check(lifetime.IsEmpty && cleanup.Reaped == count && Fds() <= baseline + 8,
                    "Full oversized exit tree failed streaming reaping or retained native descriptors");
                Console.WriteLine($"PASS All {count} adopted descendants are reaped after termination within the normal live-watch budget (reaped={cleanup.Reaped}, finalFDs={Fds()})");
                Check(string.IsNullOrEmpty(await stderr), "Native fixture reported a fork/setup failure");
            }
            finally
            {
                lifetime.Terminate();
                await root.WaitForExitAsync().WaitAsync(Limit);
                await lifetime.WaitForCleanupAsync().WaitAsync(Limit);
            }
        }
    }
    private static int Fds() => Directory.GetFiles("/proc/self/fd").Length;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
