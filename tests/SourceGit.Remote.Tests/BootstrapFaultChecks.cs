using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;
using SourceGit.RemoteClient;
using SourceGit.Server;

internal static class BootstrapFaultChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    internal static async Task ChildAsync(string recordPath, string endpoint, string nonce, string mode, string holdPath)
    {
        using var server = new RemoteServer(recordPath);
        var running = server.RunAsync();
        var record = await server.Ready.WaitAsync(Limit);
        Console.WriteLine("FIXTURE_PUBLISHED");
        using var hold = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await hold.ConnectAsync(new UnixDomainSocketEndPoint(holdPath));
        using var holdStream = new NetworkStream(hold, false);
        await holdStream.WriteAsync(BitConverter.GetBytes(Environment.ProcessId));
        if (mode != "silent")
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint));
                var pid = mode == "pid" ? record.ProcessId + 1 : record.ProcessId;
                var instance = mode == "instance" ? Guid.NewGuid().ToString("N") : record.InstanceId;
                var readyNonce = mode == "nonce" ? Guid.NewGuid().ToString("N") : nonce;
                await socket.SendAsync(Encoding.UTF8.GetBytes($"SOURCEGIT_READY {instance} {pid} {readyNonce}\n"));
            }
            catch (SocketException) { } // Parent cancellation is independent of the published server.
        }
        var stop = new byte[1];
        if (await holdStream.ReadAsync(stop) == 1 && stop[0] != 1) throw new IOException("Unexpected fixture stop command");
        await using var client = new RemoteRepositoryClient("bootstrap-fault-owner", "bootstrap-fault-recovery");
        await client.ConnectAsync("127.0.0.1", record.Port, record.Token);
        await client.ShutdownAsync();
        await running.WaitAsync(Limit);
    }

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native bootstrap fault checks require Linux");
        var root = Path.GetFullPath(Path.Combine("artifacts", "bootstrap-faults", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        foreach (var mode in new[] { "nonce", "pid", "instance", "silent", "cancel-spawn", "cancel-published", "cancel-ready", "exit-ready", "valid" })
        {
            var recordPath = Path.Combine(root, mode + ".json");
            Process? child = null;
            Socket? hold = null;
            var holdPath = "/tmp/sg-bootstrap-fault-" + Guid.NewGuid().ToString("N") + ".sock";
            using var holdListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            holdListener.Bind(new UnixDomainSocketEndPoint(holdPath)); holdListener.Listen(1);
            using var cancellation = new CancellationTokenSource();
            Task? registered = null;
            async Task RegisterHold(int pid)
            {
                hold = await holdListener.AcceptAsync().WaitAsync(Limit);
                var packet = new byte[4];
                for (var offset = 0; offset < packet.Length;)
                {
                    var read = await hold.ReceiveAsync(packet.AsMemory(offset)).AsTask().WaitAsync(Limit);
                    if (read == 0) throw new IOException("Fixture publication control closed");
                    offset += read;
                }
                Check(BitConverter.ToInt32(packet) == pid, "Published fixture process identity mismatch");
            }
            try
            {
                var hooks = new Bootstrap.Hooks
                {
                    Cancellation = cancellation.Token,
                    Deadline = mode == "silent" ? TimeSpan.FromSeconds(2) : null,
                    StartChild = original =>
                    {
                        var start = Self();
                        foreach (var argument in new[] { "--bootstrap-fault-child", "--record", recordPath, "--ready-socket", original.ArgumentList[5], "--ready-nonce", original.ArgumentList[6], "--fault-mode", mode, "--hold-socket", holdPath }) start.ArgumentList.Add(argument);
                        var launched = Process.Start(start)!;
                        child = Process.GetProcessById(launched.Id);
                        _ = child.SafeHandle;
                        registered = RegisterHold(launched.Id);
                        if (mode == "cancel-spawn") cancellation.Cancel();
                        return launched;
                    },
                    ChildStarted = async process =>
                    {
                        if (mode == "cancel-spawn") return;
                        await registered!.WaitAsync(Limit);
                        if (mode == "cancel-published") cancellation.Cancel();
                    },
                    BeforeReadyValidation = async process =>
                    {
                        if (mode == "cancel-ready") { cancellation.Cancel(); return; }
                        if (mode != "exit-ready") return;
                        await hold!.SendAsync(new byte[] { 1 });
                        await process.WaitForExitAsync().WaitAsync(Limit);
                    },
                };
                var output = new StringWriter(); var errors = new StringWriter();
                var oldOut = Console.Out; var oldError = Console.Error;
                int result;
                try
                {
                    // Bootstrap's actual transport reply contains a token; capture it without logging it.
                    Console.SetOut(output); Console.SetError(errors);
                    result = await Bootstrap.RunAsync(["--record", recordPath], hooks).WaitAsync(Limit);
                }
                finally { Console.SetOut(oldOut); Console.SetError(oldError); }
                if (registered != null) await registered.WaitAsync(Limit);
                try
                {
                    Check(child != null && result == (mode == "valid" ? 0 : 1), "Bootstrap accepted a fault or rejected valid ready");
                    Check(Directory.GetDirectories(Path.GetTempPath(), Bootstrap.EndpointPrefix(recordPath) + "*").Length == 0, "Bootstrap fault leaked its owned endpoint");
                    if (mode == "exit-ready")
                        Check(child!.HasExited && !output.ToString().Contains("SOURCEGIT_RECORD", StringComparison.Ordinal), "Ready buffered before child exit was accepted");
                    else
                    {
                        Check(!child!.HasExited && File.Exists(recordPath), "Bootstrap failure killed the independently published server or removed its record");
                        var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), RemoteJson.Default.ServerRecord)!;
                        Check(!errors.ToString().Contains(record.Token, StringComparison.Ordinal), "Bootstrap diagnostic exposed the connection token");
                        await using var probe = new RemoteRepositoryClient("bootstrap-fault-owner", "bootstrap-fault-recovery");
                        await probe.ConnectAsync("127.0.0.1", record.Port, record.Token);
                        Check((await probe.GetEnvironmentAsync()).Home.Length > 0, "Published server was unusable after bootstrap reply/failure");
                    }
                    Check(output.ToString().Split('\n').Count(line => line.StartsWith("SOURCEGIT_RECORD ", StringComparison.Ordinal)) == (mode == "valid" ? 1 : 0), "Ready result was published more than once or a failure exposed a successful record reply");
                    Console.WriteLine("PASS Actual bootstrap " + mode + " validates once, reclaims its endpoint and preserves published-server ownership");
                }
                finally { output.Dispose(); errors.Dispose(); }
            }
            finally
            {
                hold?.Dispose();
                if (child != null)
                {
                    await child.WaitForExitAsync().WaitAsync(Limit);
                    child.Dispose();
                }
                File.Delete(holdPath);
            }
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var record = Path.Combine(root, "cancel-endpoint.json"); var spawned = false;
            var hooks = new Bootstrap.Hooks
            {
                Cancellation = cancellation.Token,
                EndpointRegistered = _ => { cancellation.Cancel(); return Task.CompletedTask; },
                StartChild = _ => { spawned = true; throw new Exception("Canceled bootstrap started a child"); },
            };
            var oldError = Console.Error; using var error = new StringWriter(); int result;
            try { Console.SetError(error); result = await Bootstrap.RunAsync(["--record", record], hooks); }
            finally { Console.SetError(oldError); }
            Check(result == 1 && !spawned && !File.Exists(record) && Directory.GetDirectories(Path.GetTempPath(), Bootstrap.EndpointPrefix(record) + "*").Length == 0,
                "Cancellation after endpoint registration created a server or leaked its endpoint");
            Console.WriteLine("PASS Cancellation after actual bootstrap endpoint registration reclaims resources before child spawn without publishing a record");
        }
    }

    private static ProcessStartInfo Self()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet") start.ArgumentList.Add(typeof(BootstrapFaultChecks).Assembly.Location);
        return start;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
