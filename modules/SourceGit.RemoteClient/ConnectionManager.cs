using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

public sealed record ConnectionOptions(string Id, string Name, string Kind, string Target, string? User = null, string? PackagePath = null, string? PackageSha256 = null, string? LocalServerPath = null, string? SshOptions = null, bool InteractiveSsh = false, string? SshPassword = null);
/// <summary>Windows DPAPI helpers for the per-connection stored password (CurrentUser scope: only this OS user can decrypt).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ConnectionCredential
{
    public static string Encrypt(string plain) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
    public static string Decrypt(string cipher) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
}
public sealed record ConnectionDiagnostic(DateTime Time, string Stage, string Message);
[JsonSerializable(typeof(ConnectionOptions))]
[JsonSerializable(typeof(ConnectionOptions[]))]
public partial class ConnectionJson : JsonSerializerContext { }

/// <summary>Bootstraps a detached server, then uses TCP (WSL) or a dedicated SSH forwarding channel.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ConnectionOptions _options;
    private static readonly string GuiOwner = Guid.NewGuid().ToString("N");
    private static readonly string GuiRecovery = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _owner = GuiOwner;
    private readonly OperationTracker _operations = new();
    private readonly string _recovery = GuiRecovery;
    private Process? _tunnel;
    private LocalServerLauncher? _localLauncher;
    private CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private Task? _reconnectTask;
    private readonly SemaphoreSlim _connectGate = new(1);
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource<RemoteRepositoryClient> _recovered = NewRecovery();
    private Task? _disposeTask;
    private static TaskCompletionSource<RemoteRepositoryClient> NewRecovery() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _reconnecting;
    private readonly TimeSpan _recoveryTimeout;
    private string _runtime = "linux-x64";
    public RemoteRepositoryClient? Client { get; private set; }
    public string State { get; private set; } = "Disconnected";
    public event Action<ConnectionDiagnostic>? Diagnostic;
    public event Action<string>? StateChanged;
    public event Action<RemoteRepositoryClient, bool>? Connected;
    public ConnectionOptions Options => _options;
    internal Func<CancellationToken, Task>? BeforeClientInstalled { get; set; }
    internal Func<Task>? BeforeRecoveryRetired { get; set; }
    internal Action<LocalServerLauncher>? ConfigureLocalLauncher { get; set; }
    public ConnectionManager(ConnectionOptions options) : this(options, TimeSpan.FromMinutes(5)) { }
    internal ConnectionManager(ConnectionOptions options, TimeSpan recoveryTimeout)
    {
        if (recoveryTimeout <= TimeSpan.Zero || recoveryTimeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(recoveryTimeout));
        _options = options; _lifetimeToken = _lifetime.Token; _recoveryTimeout = recoveryTimeout;
    }
    private void SetState(string state) { if (Volatile.Read(ref _disposed) != 0) return; State = state; StateChanged?.Invoke(state); Log(state, state); }
    private void Log(string stage, string message) => Diagnostic?.Invoke(new ConnectionDiagnostic(DateTime.UtcNow, stage, message));
    public async Task<RemoteRepositoryClient> ConnectAsync(CancellationToken cancellation = default)
    {
        CancellationTokenSource connectionCancellation;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetimeToken);
        }
        using var linked = connectionCancellation;
        await _connectGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var previousInstance = Client?.InstanceId;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
                if (_recovered.Task.IsCompleted && Client is not { IsDisconnected: false }) _recovered = NewRecovery();
            SetState("Connecting");
            ServerDeployer? deployer = null;
            ServerDeployment? discoveredDeployment = null;
            var upgradeRequired = false;
            ServerRecord record;
            if (_options.Kind.Equals("Local", StringComparison.OrdinalIgnoreCase)) record = await StartLocal(linked.Token).ConfigureAwait(false);
            else
            {
                var environment = await RunRemote("uname -s; uname -m; uname -r; command -v git; command -v tar; command -v sha256sum; command -v flock; command -v setsid; command -v nohup", null, linked.Token).ConfigureAwait(false);
                var lines = environment.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 9 || lines[0] != "Linux") throw new IOException("Remote Linux 5.4+, Git, tar, sha256sum, flock, setsid and nohup are required");
                if (lines[1] is not ("x86_64" or "aarch64")) throw new IOException("Unsupported remote architecture: " + lines[1]);
                var kernel = lines[2].Split('.');
                if (kernel.Length < 2 || !int.TryParse(kernel[0], out var major) || !int.TryParse(kernel[1], out var minor) || major < 5 || major == 5 && minor < 4)
                    throw new IOException("Linux 5.4+ is required for stable pidfd descendant cleanup; detected " + lines[2]);
                _runtime = lines[1] == "aarch64" ? "linux-arm64" : "linux-x64";
                Log("Environment", "Linux " + lines[1] + "; Git detected");
                SetState("Installing");
                deployer = new ServerDeployer(RunRemote);
                var package = _options.PackagePath ?? Path.Combine(AppContext.BaseDirectory, "server", "sourcegit-server-" + _runtime + ".tar.gz");
                await deployer.InstallAsync(package, _options.PackageSha256, linked.Token).ConfigureAwait(false);
                SetState("Starting");
                var deployment = await deployer.StartAsync(linked.Token).ConfigureAwait(false);
                discoveredDeployment = deployment;
                record = deployment.Record;
                upgradeRequired = deployment.UpgradeRequired;
            }
            for (var upgradeAttempt = 0; ; upgradeAttempt++)
            {
                var existing = Client is { IsDisconnected: false } live && live.InstanceId == record.InstanceId ? live : null;
                if (!upgradeRequired && existing != null) { linked.Token.ThrowIfCancellationRequested(); SetState("Connected"); return existing; }
                var client = existing ?? await OpenChannelAsync(record, linked.Token).ConfigureAwait(false);
                if (upgradeRequired)
                {
                    if (upgradeAttempt != 0)
                    {
                        if (client != Client) await client.DisposeAsync().ConfigureAwait(false);
                        throw new IOException("Server upgrade did not select the installed package");
                    }
                    await ShutdownForUpgradeAsync(client, linked.Token).ConfigureAwait(false);
                    SetState("Starting");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var deployment = await deployer!.StartAsync(deadline.Token, discoveredDeployment).ConfigureAwait(false);
                    if (deployment.Record.InstanceId == record.InstanceId) throw new IOException("Authenticated upgrade returned the old server instance");
                    discoveredDeployment = deployment;
                    record = deployment.Record;
                    upgradeRequired = deployment.UpgradeRequired;
                    continue;
                }
                var changed = previousInstance != client.InstanceId;
                if (BeforeClientInstalled != null)
                {
                    try { await BeforeClientInstalled(linked.Token).ConfigureAwait(false); }
                    catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
                }
                if (Client != null) { Client.Disconnected -= OnDisconnected; await Client.DisposeAsync().ConfigureAwait(false); }
                lock (_lifecycleGate)
                {
                    if (_disposed == 0 && !linked.IsCancellationRequested)
                    {
                        Client = client;
                        client.RecoveryClient = WaitForRecoveredClient;
                        client.Disconnected += OnDisconnected;
                        SetState("Connected");
                        // StateChanged is user code and may reenter Dispose.
                        if (_disposed == 0)
                        {
                            _recovered.TrySetResult(client);
                            Connected?.Invoke(client, changed);
                            if (_disposed == 0) return client;
                        }
                    }
                }
                await client.DisposeAsync().ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(ConnectionManager));
            }
        }
        catch (Exception ex)
        {
            SetState(Client is { IsDisconnected: false } ? "Connected" : "Failed");
            lock (_lifecycleGate)
                if (!_reconnecting && _disposed == 0 && State == "Failed") _recovered.TrySetException(ex);
            Log("Error", ex.Message); throw;
        }
        finally { _connectGate.Release(); }
    }
    private async Task<RemoteRepositoryClient> OpenChannelAsync(ServerRecord record, CancellationToken cancellation)
    {
        var port = record.Port;
        if (_options.Kind.Equals("Ssh", StringComparison.OrdinalIgnoreCase))
        {
            SetState("Opening tunnel");
            using (var free = new TcpListener(IPAddress.Loopback, 0)) { free.Start(); port = ((IPEndPoint)free.LocalEndpoint).Port; }
            var start = SshStart();
            start.ArgumentList.Add("-N");
            foreach (var arg in SshOptions("ExitOnForwardFailure=yes", "ServerAliveInterval=15", "ServerAliveCountMax=3")) start.ArgumentList.Add(arg);
            start.ArgumentList.Add("-L"); start.ArgumentList.Add($"127.0.0.1:{port}:127.0.0.1:{record.Port}");
            if (!string.IsNullOrEmpty(_options.User)) { start.ArgumentList.Add("-l"); start.ArgumentList.Add(_options.User); }
            start.ArgumentList.Add("--"); start.ArgumentList.Add(_options.Target);
            // Kill, not just dispose: Process.Dispose does not terminate a running tunnel, and a leaked ssh process keeps its -L forwarding alive.
            if (_tunnel != null)
            {
                try { if (!_tunnel.HasExited) _tunnel.Kill(true); } catch (InvalidOperationException) { }
                _tunnel.Dispose();
            }
            _tunnel = Process.Start(start) ?? throw new IOException("Cannot start SSH tunnel");
            _ = DrainTunnel(_tunnel);
        }
        SetState("Synchronizing");
        var client = new RemoteRepositoryClient(_owner, _recovery, operations: _operations);
        try
        {
            Exception? failure = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try { await client.ConnectAsync("127.0.0.1", port, record.Token, cancellation).ConfigureAwait(false); failure = null; break; }
                catch (SocketException ex)
                { failure = ex; Log("Connecting", "connect attempt " + (attempt + 1) + "/50 failed: " + ex.Message); await client.DisposeAsync().ConfigureAwait(false); client = new RemoteRepositoryClient(_owner, _recovery, operations: _operations); await Task.Delay(100, cancellation).ConfigureAwait(false); }
            }
            if (failure != null) throw new IOException("Remote localhost is unreachable. Check WSL 2 localhost forwarding or SSH tunnel diagnostics.", failure);
            if (client.InstanceId != record.InstanceId) { await client.DisposeAsync().ConfigureAwait(false); throw new IOException("Server instance mismatch; SSH target may route to another host"); }
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal async Task ShutdownForUpgradeAsync(RemoteRepositoryClient client, CancellationToken cancellation = default)
    {
        // An intentional EOF can arrive before the Shutdown reply continuation.
        // Suppress its handler before sending, rather than after acceptance.
        client.Disconnected -= OnDisconnected;
        try
        {
            // Handshake enforces ownership; rejection leaves the current client usable.
            await client.ShutdownAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            lock (_lifecycleGate)
            {
                if (_disposed == 0 && client == Client)
                {
                    client.Disconnected += OnDisconnected;
                    if (client.IsDisconnected) OnDisconnected(new IOException("Upgrade channel closed before acknowledgement"));
                }
            }
            if (client != Client) await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var previous = Client;
        Client = null;
        if (previous != null) previous.Disconnected -= OnDisconnected;
        SetState("Starting");
        if (previous != null && previous != client) await previous.DisposeAsync().ConfigureAwait(false);
        await client.DisposeAsync().ConfigureAwait(false);
    }
    private async Task<RemoteRepositoryClient> WaitForRecoveredClient(CancellationToken cancellation)
    {
        Task<RemoteRepositoryClient> recovery;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (State == "Connected" && Client is { IsDisconnected: false } live) return live;
            if (_recovered.Task.IsCompletedSuccessfully) _recovered = NewRecovery();
            recovery = _recovered.Task;
        }
        // The recovery worker owns the relative budget. An independent waiter
        // deadline could race its authoritative failure/state publication.
        return await recovery.WaitAsync(cancellation).ConfigureAwait(false);
    }
    private void OnDisconnected(Exception error)
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0 || _reconnecting) return;
            _reconnecting = true;
            if (_recovered.Task.IsCompleted) _recovered = NewRecovery();
            _reconnectTask = Task.Run(async () =>
            {
                // One relative deadline covers connection attempts and failure
                // backoff. Wall-clock adjustments cannot extend recovery.
                using var budget = new CancellationTokenSource(_recoveryTimeout);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, budget.Token);
                IOException? failure = null;
                try
                {
                    SetState("Reconnecting"); Log("Disconnect", error.Message);
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        try
                        {
                            if (Client != null) Client.Disconnected -= OnDisconnected;
                            await ConnectAsync(deadline.Token).ConfigureAwait(false);
                            if (BeforeRecoveryRetired != null) await BeforeRecoveryRetired().ConfigureAwait(false);
                            return;
                        }
                        catch (Exception ex) when (!_lifetimeToken.IsCancellationRequested && State == "Connected" && Client is { IsDisconnected: false })
                        {
                            try { Log("Connected callback", ex.GetType().Name); } catch { }
                            // Installation already saved/published its verified
                            // channel. A later callback error/deadline cannot
                            // retroactively replace that successful generation.
                            return;
                        }
                        catch (Exception) when (deadline.IsCancellationRequested) { deadline.Token.ThrowIfCancellationRequested(); throw; }
                        catch (Exception ex) { Log("Reconnect", ex.Message); await Task.Delay(2000, deadline.Token).ConfigureAwait(false); }
                    }
                }
                catch (OperationCanceledException) when (!_lifetimeToken.IsCancellationRequested)
                { failure = new IOException("Connection could not recover within " + _recoveryTimeout.TotalSeconds.ToString("g", System.Globalization.CultureInfo.InvariantCulture) + " seconds; operation outcome remains unknown"); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                { failure = new IOException("Connection recovery failed; operation outcome remains unknown", ex); }
                finally
                {
                    lock (_lifecycleGate)
                    {
                        _reconnecting = false;
                        if (_disposed == 0 && failure != null)
                        {
                            // Retire the worker before waking its waiters; a
                            // subsequent generation must not inherit this flag.
                            State = "Failed";
                            _recovered.TrySetException(failure);
                            try { SetState("Failed"); } catch { }
                        }
                        else if (_disposed == 0 && Client is { IsDisconnected: true })
                        {
                            // A newly installed channel may disconnect while this
                            // worker still owns _reconnecting. Retiring the worker
                            // must hand that new generation back to recovery.
                            OnDisconnected(new IOException("Installed connection disconnected before recovery completed"));
                        }
                    }
                }
            });
        }
    }
    private async Task DrainTunnel(Process tunnel)
    {
        try
        {
            var output = ReadPipe(false);
            var stderr = ReadPipe(true);
            // Observe both readers even when closing the process breaks one pipe.
            await Task.WhenAll(output, stderr).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error)) Log("SSH", error.Trim());
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
                try { Log("SSH drain", error.GetType().Name); } catch { }
        }
        // Convert even synchronous reader/property failures into tasks so both
        // pipes always reach WhenAll and every exception is observed.
        async Task<string> ReadPipe(bool standardError) =>
            await (standardError ? tunnel.StandardError : tunnel.StandardOutput).ReadToEndAsync().ConfigureAwait(false);
    }
    private async Task<ServerRecord> StartLocal(CancellationToken cancellation)
    {
        var executable = _options.LocalServerPath ?? Path.Combine(AppContext.BaseDirectory, "server", "sourcegit-server.exe");
        var directory = Path.Combine(Path.GetTempPath(), "sourcegit-server", _options.Id);
        if (_localLauncher == null)
        {
            _localLauncher = new LocalServerLauncher(executable, Path.Combine(directory, "instance.json"), message => Log("Local startup", message));
            ConfigureLocalLauncher?.Invoke(_localLauncher);
        }
        return await _localLauncher.StartAsync(async (record, token) =>
        {
            var client = Client is { IsDisconnected: false } live && live.InstanceId == record.InstanceId
                ? live : await OpenChannelAsync(record, token).ConfigureAwait(false);
            await ShutdownForUpgradeAsync(client, token).ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }
    private async Task<string> RunRemote(string script, byte[]? input, CancellationToken cancellation)
    {
        script = script.Replace("\r\n", "\n");
        ProcessStartInfo start;
        if (_options.Kind.Equals("Wsl", StringComparison.OrdinalIgnoreCase))
        {
            start = StartInfo("wsl.exe"); start.ArgumentList.Add("--distribution"); start.ArgumentList.Add(_options.Target);
            if (!string.IsNullOrEmpty(_options.User)) { start.ArgumentList.Add("--user"); start.ArgumentList.Add(_options.User); }
            foreach (var arg in new[] { "--exec", "sh", "-c", script }) start.ArgumentList.Add(arg);
        }
        else
        {
            start = SshStart(); start.ArgumentList.Add("-T");
            foreach (var arg in SshOptions("ConnectTimeout=15")) start.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(_options.User)) { start.ArgumentList.Add("-l"); start.ArgumentList.Add(_options.User); }
            start.ArgumentList.Add("--"); start.ArgumentList.Add(_options.Target); start.ArgumentList.Add("sh -c " + Quote(script));
        }
        using var process = Process.Start(start) ?? throw new IOException("Cannot start remote launcher");
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        if (input != null) await process.StandardInput.BaseStream.WriteAsync(input, cancellation).ConfigureAwait(false);
        process.StandardInput.Close();
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        var result = await output.ConfigureAwait(false); var diagnostics = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = diagnostics.Trim();
            if (_options.Kind.Equals("Ssh", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(_options.SshPassword) && !_options.InteractiveSsh && (detail.Contains("Permission denied") || detail.Contains("Host key verification failed")))
                detail += " (key/agent authentication failed or the host key is not trusted yet: set a password for this connection, enable terminal login, or trust the host by running 'ssh " + _options.Target + "' once in a terminal)";
            throw new IOException("Remote launcher failed: " + detail);
        }
        if (!string.IsNullOrWhiteSpace(diagnostics)) Log("Launcher", diagnostics.Trim());
        return result;
    }
    private static ProcessStartInfo StartInfo(string command, bool console = false) => new(command) { UseShellExecute = false, CreateNoWindow = !console, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    private string? _askpassBat;
    private string? _askpassFile;
    // Windows OpenSSH (Win32-OpenSSH) does not implement ControlMaster multiplexing (any ControlPath
    // fails with "getsockname failed: Not a socket"), so every ssh process authenticates on its own.
    // Stored-password mode injects the password through SSH_ASKPASS (silent, one dialog entry covers
    // the launcher and the tunnel alike); terminal mode drops BatchMode so OpenSSH prompts natively in
    // an attached console (password and first host-key confirmation, OpenSSH's own UI).
    private ProcessStartInfo SshStart()
    {
        var console = _options.InteractiveSsh && string.IsNullOrEmpty(_options.SshPassword);
        var start = StartInfo("ssh", console);
        if (!string.IsNullOrEmpty(_options.SshPassword))
        {
            EnsureAskpass();
            start.Environment["SSH_ASKPASS"] = _askpassBat;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        }
        return start;
    }
    // The password goes through a side file: a .bat built from the password breaks on shell metacharacters
    // (& % ^) and would mangle legitimate passwords.
    private void EnsureAskpass()
    {
        if (_askpassBat != null) return;
        if (!OperatingSystem.IsWindows()) throw new IOException("Stored SSH passwords are only available on Windows; clear the password and use terminal login on this system.");
        string password;
        try { password = ConnectionCredential.Decrypt(_options.SshPassword!); }
        catch (Exception ex) { throw new IOException("The stored password for this connection cannot be decrypted on this machine; enter it again in the connection dialog.", ex); }
        if (string.IsNullOrEmpty(password)) throw new IOException("The stored password for this connection is empty; enter it again in the connection dialog.");
        _askpassFile = Path.Combine(Path.GetTempPath(), "sourcegit-ssh-" + _options.Id + "-" + Guid.NewGuid().ToString("N") + ".txt");
        _askpassBat = _askpassFile + ".bat";
        File.WriteAllText(_askpassFile, password);
        File.WriteAllText(_askpassBat, "@type \"" + _askpassFile + "\"\n");
    }
    private void DeleteAskpass()
    {
        if (_askpassBat != null) { try { File.Delete(_askpassBat); if (_askpassFile != null) File.Delete(_askpassFile); } catch (IOException) { } _askpassBat = null; _askpassFile = null; }
    }
    private IEnumerable<string> SshOptions(params string[] extra)
    {
        if (string.IsNullOrEmpty(_options.SshPassword) && !_options.InteractiveSsh) { yield return "-o"; yield return "BatchMode=yes"; }
        foreach (var value in extra) { yield return "-o"; yield return value; }
        if (!string.IsNullOrEmpty(_options.SshPassword) || _options.InteractiveSsh) { yield return "-o"; yield return "NumberOfPasswordPrompts=1"; }
        // Stored-password mode has no terminal for OpenSSH's first host-key prompt; accept the first
        // key automatically (subsequent changes still fail) and tell the user how to pin options.
        if (!string.IsNullOrEmpty(_options.SshPassword)) { yield return "-o"; yield return "StrictHostKeyChecking=accept-new"; }
        if (!string.IsNullOrWhiteSpace(_options.SshOptions))
            foreach (var token in _options.SshOptions!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return token;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private int _disposed;
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = 1;
            _recovered.TrySetCanceled();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }
    private async Task DisposeCoreAsync()
    {
        // Leave the caller's lifecycle lock before invoking cancellation callbacks.
        await Task.Yield();
        _lifetime.Cancel();
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DeleteAskpass();
            var client = Client; Client = null;
            if (client != null) { client.Disconnected -= OnDisconnected; await client.DisposeAsync().ConfigureAwait(false); }
            if (_tunnel != null) { try { if (!_tunnel.HasExited) _tunnel.Kill(true); } catch (InvalidOperationException) { } _tunnel.Dispose(); }
            // Server continues outstanding work and exits after its configurable idle grace.
            if (_localLauncher != null) await _localLauncher.DisposeAsync().ConfigureAwait(false);
            State = "Disconnected";
        }
        finally { _connectGate.Release(); }
        Task? reconnect;
        lock (_lifecycleGate) reconnect = _reconnectTask;
        if (reconnect != null) await reconnect.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
