using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceGit.Contracts;

namespace SourceGit.Server;

internal static class Bootstrap
{
    internal sealed class Hooks
    {
        internal CancellationToken Cancellation { get; init; }
        internal TimeSpan? Deadline { get; init; }
        internal Func<string, Task>? EndpointRegistered { get; init; }
        internal Func<ProcessStartInfo, Process>? StartChild { get; init; }
        internal Func<Process, Task>? ChildStarted { get; init; }
        internal Func<Process, Task>? BeforeReadyValidation { get; init; }
    }

    internal static async Task<int> RunAsync(string[] arguments, Hooks? hooks = null)
    {
        string? Option(string key) { var index = Array.IndexOf(arguments, key); return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null; }
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Remote bootstrap requires Linux");
            var recordPath = Path.GetFullPath(Option("--record") ?? throw new ArgumentException("Bootstrap record path required"));
            var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Bootstrap executable unavailable"));
            var basePath = Path.GetDirectoryName(recordPath)!;
            var oldPid = Option("--old-pid");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(hooks?.Cancellation ?? default);
            deadline.CancelAfter(hooks?.Deadline ?? TimeSpan.FromSeconds(oldPid == null ? 10 : 15));
            using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal => { signal.Cancel = true; deadline.Cancel(); });
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; deadline.Cancel(); });
            var cancellation = deadline.Token;
            BootstrapEndpoint.Reconcile(recordPath, cancellation);
            if (oldPid != null)
                await LinuxProcessExit.WaitAsync(int.Parse(oldPid, System.Globalization.CultureInfo.InvariantCulture),
                    Option("--old-start") ?? throw new ArgumentException("Old process identity required"), cancellation,
                    arguments.Contains("--wait-only", StringComparer.Ordinal) ? () => Console.WriteLine("SOURCEGIT_EXIT_WAIT_REGISTERED") : null);
            if (arguments.Contains("--wait-only", StringComparer.Ordinal))
            {
                if (oldPid == null) throw new ArgumentException("Wait-only requires the old process identity");
                return 0;
            }

            if (File.Exists(recordPath))
            {
                var previous = await ReadRecord(recordPath, cancellation);
                var processPath = $"/proc/{previous.ProcessId}";
                if (Directory.Exists(processPath))
                {
                    var running = File.ResolveLinkTarget(processPath + "/exe", false)?.FullName ?? throw new IOException("Cannot verify running server executable");
                    var command = await File.ReadAllTextAsync(processPath + "/cmdline", cancellation);
                    if (!running.StartsWith(basePath + "/versions/", StringComparison.Ordinal) || !running.EndsWith("/sourcegit-server", StringComparison.Ordinal) ||
                        !command.Split('\0').Contains(recordPath, StringComparer.Ordinal))
                        throw new IOException("Live record process cannot be verified; record preserved");
                    var identity = LinuxProcessExit.StartIdentity(previous.ProcessId);
                    if (running != executable) Console.WriteLine("SOURCEGIT_UPGRADE_REQUIRED");
                    Console.WriteLine("SOURCEGIT_PROCESS_START " + identity);
                    cancellation.ThrowIfCancellationRequested();
                    WriteRecord(previous);
                    return 0;
                }
                // Participants hold the same start.lock. An external record change
                // must never be mistaken for the stale record we inspected.
                var current = await ReadRecord(recordPath, cancellation);
                if (current.InstanceId != previous.InstanceId || current.ProcessId != previous.ProcessId)
                    throw new IOException("Record changed during discovery; record preserved");
                File.Delete(recordPath);
            }

            using var ownedEndpoint = new BootstrapEndpoint(recordPath);
            var nonce = ownedEndpoint.Nonce;
            var endpoint = ownedEndpoint.SocketPath;
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint));
            File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(1); // Registered before launching the detached child.
            if (hooks?.EndpointRegistered != null) await hooks.EndpointRegistered(endpoint);
            cancellation.ThrowIfCancellationRequested();

            var logPath = Path.Combine(basePath, "server-" + Protocol.Build + ".log");
            var logOffset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
            var start = new ProcessStartInfo("sh") { UseShellExecute = false };
            foreach (var value in new[] { "-c", "exec setsid --wait nohup \"$1\" --record \"$2\" --ready-socket \"$3\" --ready-nonce \"$4\" >>\"$5\" 2>&1 </dev/null 9>&-", "sourcegit-bootstrap",
                executable, recordPath, endpoint, nonce, logPath }) start.ArgumentList.Add(value);
            using var child = (hooks?.StartChild == null ? Process.Start(start) : hooks.StartChild(start)) ?? throw new IOException("Detached server did not start");
            if (hooks?.ChildStarted != null) await hooks.ChildStarted(child);
            using var readyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var accepted = listener.AcceptAsync(readyCancellation.Token).AsTask();
            var exited = child.WaitForExitAsync(readyCancellation.Token);
            try
            {
                var winner = await Task.WhenAny(accepted, exited);
                if (winner == exited)
                {
                    await exited;
                    throw new IOException("Detached server exited before ready (PID " + child.Id + ", exit " + child.ExitCode + "): " + StartupTail(logPath, logOffset));
                }
                using var connection = await accepted;
                using var stream = new NetworkStream(connection, false);
                var bytes = new byte[512]; var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), cancellation);
                    if (read == 0) break;
                    count += read;
                    if (Array.IndexOf(bytes, (byte)'\n', 0, count) >= 0) break;
                }
                var signal = Encoding.UTF8.GetString(bytes, 0, count).Trim().Split(' ');
                var record = await ReadRecord(recordPath, cancellation);
                if (hooks?.BeforeReadyValidation != null) await hooks.BeforeReadyValidation(child);
                cancellation.ThrowIfCancellationRequested();
                if (signal.Length != 4 || signal[0] != "SOURCEGIT_READY" || signal[1] != record.InstanceId ||
                    signal[2] != record.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) || signal[3] != nonce ||
                    record.ProcessId != child.Id || child.HasExited)
                    throw new IOException("Detached server ready identity mismatch; record preserved");
                Console.WriteLine("SOURCEGIT_PROCESS_START " + LinuxProcessExit.StartIdentity(record.ProcessId));
                cancellation.ThrowIfCancellationRequested();
                WriteRecord(record);
                return 0;
            }
            finally
            {
                readyCancellation.Cancel();
                try { await exited; } catch (OperationCanceledException) { }
                if (!accepted.IsCompletedSuccessfully)
                {
                    try { using var abandoned = await accepted; } catch (OperationCanceledException) { }
                }
                else accepted.Result.Dispose();
            }
        }
        catch (Exception error)
        {
            // Record tokens/ready nonces never belong in diagnostic output.
            Console.Error.WriteLine("SOURCEGIT_BOOTSTRAP " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static async Task<ServerRecord> ReadRecord(string path, CancellationToken token) =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), RemoteJson.Default.ServerRecord) ?? throw new IOException("Invalid startup record");
    private static void WriteRecord(ServerRecord record) => Console.WriteLine("SOURCEGIT_RECORD " + JsonSerializer.Serialize(record, RemoteJson.Default.ServerRecord));
    private static string StartupTail(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = Math.Min(stream.Length, Math.Max(offset, stream.Length - 4096));
            var bytes = new byte[4096];
            var count = stream.Read(bytes, 0, bytes.Length);
            return string.Join("\n", Encoding.UTF8.GetString(bytes, 0, count).Split('\n').TakeLast(30));
        }
        catch (IOException) { return "startup log unavailable"; }
    }

    internal static string EndpointPrefix(string recordPath) => "sg-ready-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(recordPath))))[..16].ToLowerInvariant() + "-";

    internal static async Task NotifyReadyAsync(string? endpoint, string? nonce, ServerRecord record, string recordPath)
    {
        if (endpoint == null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), timeout.Token);
            using var stream = new NetworkStream(socket, false);
            var signal = Encoding.UTF8.GetBytes($"SOURCEGIT_READY {record.InstanceId} {record.ProcessId} {nonce}\n");
            await stream.WriteAsync(signal, timeout.Token);
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
        {
            // Bootstrap cancellation/SSH loss must not kill a published independent server.
            Console.Error.WriteLine("SOURCEGIT_READY_NOTIFY " + error.GetType().Name + ": endpoint unavailable");
        }
        finally
        {
            // If the bootstrap transport was lost abruptly, its published child
            // also owns cleanup of this exact nonce-scoped endpoint, never other paths.
            if (nonce != null && nonce.Length == 32 && Guid.TryParseExact(nonce, "N", out _))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(endpoint));
                if (Path.GetFileName(endpoint) == "ready" && directory != null && Path.GetFileName(directory).EndsWith("-" + nonce, StringComparison.Ordinal))
                {
                    try { BootstrapEndpoint.TryCleanStale(directory, recordPath); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
    }
}
