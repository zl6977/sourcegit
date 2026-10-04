using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly string _recovery = GuiRecovery;
    private Process? _tunnel;
    private Process? _localServer;
    private CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectGate = new(1);
    private bool _reconnecting;
    private string _runtime = "linux-x64";
    public RemoteRepositoryClient? Client { get; private set; }
    public string State { get; private set; } = "Disconnected";
    public event Action<ConnectionDiagnostic>? Diagnostic;
    public event Action<string>? StateChanged;
    public event Action<RemoteRepositoryClient, bool>? Connected;
    public ConnectionOptions Options => _options;
    public ConnectionManager(ConnectionOptions options) { _options = options; }
    private void SetState(string state) { State = state; StateChanged?.Invoke(state); Log(state, state); }
    private void Log(string stage, string message) => Diagnostic?.Invoke(new ConnectionDiagnostic(DateTime.UtcNow, stage, message));
    public async Task<RemoteRepositoryClient> ConnectAsync(CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        SetState("Connecting");
        await _connectGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ServerRecord record;
            if (_options.Kind.Equals("Local", StringComparison.OrdinalIgnoreCase)) record = await StartLocal(linked.Token).ConfigureAwait(false);
            else
            {
                var environment = await RunRemote("uname -s; uname -m; command -v git; command -v tar; command -v sha256sum", null, linked.Token).ConfigureAwait(false);
                var lines = environment.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 5 || lines[0] != "Linux") throw new IOException("Remote Linux, Git, tar and sha256sum are required");
                if (lines[1] is not ("x86_64" or "aarch64")) throw new IOException("Unsupported remote architecture: " + lines[1]);
                _runtime = lines[1] == "aarch64" ? "linux-arm64" : "linux-x64";
                Log("Environment", "Linux " + lines[1] + "; Git detected");
                record = await Bootstrap(linked.Token).ConfigureAwait(false);
            }
            var port = record.Port;
            // Same live instance: the reconnect loop or a second caller raced us; reuse the current channel instead of tunneling again.
            if (Client is { IsDisconnected: false } live && live.InstanceId == record.InstanceId) return live;
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
            var client = new RemoteRepositoryClient(_owner, _recovery);
            Exception? failure = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try { await client.ConnectAsync("127.0.0.1", port, record.Token, linked.Token).ConfigureAwait(false); failure = null; break; }
                catch (SocketException ex)
                { failure = ex; Log("Connecting", "connect attempt " + (attempt + 1) + "/50 failed: " + ex.Message); await client.DisposeAsync().ConfigureAwait(false); client = new RemoteRepositoryClient(_owner, _recovery); await Task.Delay(100, linked.Token).ConfigureAwait(false); }
            }
            if (failure != null) throw new IOException("Remote localhost is unreachable. Check WSL 2 localhost forwarding or SSH tunnel diagnostics.", failure);
            if (client.InstanceId != record.InstanceId) { await client.DisposeAsync().ConfigureAwait(false); throw new IOException("Server instance mismatch; SSH target may route to another host"); }
            var changed = Client?.InstanceId != client.InstanceId;
            if (Client != null) await Client.DisposeAsync().ConfigureAwait(false);
            Client = client;
            client.RecoveryClient = WaitForRecoveredClient;
            client.Disconnected += OnDisconnected;
            SetState("Connected"); Connected?.Invoke(client, changed);
            return client;
        }
        catch (Exception ex) { SetState("Failed"); Log("Error", ex.Message); throw; }
        finally { _connectGate.Release(); }
    }
    private async Task<RemoteRepositoryClient> WaitForRecoveredClient(CancellationToken cancellation)
    {
        var until = DateTime.UtcNow.AddMinutes(5);
        while (!_lifetime.IsCancellationRequested && DateTime.UtcNow < until)
        {
            if (State == "Connected" && Client is { IsDisconnected: false } live) return live;
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        throw new IOException("Connection could not recover; operation outcome remains unknown");
    }
    private void OnDisconnected(Exception error)
    {
        if (_lifetime.IsCancellationRequested || _reconnecting) return;
        _reconnecting = true;
        _ = Task.Run(async () =>
        {
            SetState("Reconnecting"); Log("Disconnect", error.Message);
            var until = DateTime.UtcNow.AddMinutes(5);
            try
            {
                while (!_lifetime.IsCancellationRequested && DateTime.UtcNow < until)
                {
                    try { if (Client != null) Client.Disconnected -= OnDisconnected; await ConnectAsync(_lifetime.Token).ConfigureAwait(false); return; }
                    catch (Exception ex) { Log("Reconnect", ex.Message); await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false); }
                }
                SetState("Failed");
            }
            catch (OperationCanceledException) { }
            finally { _reconnecting = false; }
        });
    }
    private async Task DrainTunnel(Process tunnel)
    {
        var output = tunnel.StandardOutput.ReadToEndAsync();
        var stderr = tunnel.StandardError.ReadToEndAsync();
        await output.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error)) Log("SSH", error.Trim());
    }
    private async Task<ServerRecord> StartLocal(CancellationToken cancellation)
    {
        var executable = _options.LocalServerPath ?? Path.Combine(AppContext.BaseDirectory, "server", "sourcegit-server.exe");
        var directory = Path.Combine(Path.GetTempPath(), "sourcegit-server", _options.Id);
        Directory.CreateDirectory(directory);
        var record = Path.Combine(directory, "instance.json");
        if (File.Exists(record))
        {
            var old = JsonSerializer.Deserialize(await File.ReadAllTextAsync(record, cancellation).ConfigureAwait(false), RemoteJson.Default.ServerRecord);
            if (old != null && old.Build == Protocol.Build && IsAlive(old.ProcessId))
            {
                try { var probe = await RemoteRepositoryClient.ProbeAsync("127.0.0.1", old.Port, old.Token, cancellation).ConfigureAwait(false); if (probe.InstanceId == old.InstanceId) return old; }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { if (cancellation.IsCancellationRequested) throw; }
            }
            File.Delete(record);
        }
        var start = StartInfo(executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable);
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(executable);
        start.ArgumentList.Add("--record"); start.ArgumentList.Add(record);
        _localServer = Process.Start(start) ?? throw new IOException("Cannot start local server");
        _ = _localServer.StandardOutput.ReadToEndAsync(); _ = _localServer.StandardError.ReadToEndAsync();
        for (var i = 0; i < 100; i++) { if (File.Exists(record)) return JsonSerializer.Deserialize(await File.ReadAllTextAsync(record, cancellation).ConfigureAwait(false), RemoteJson.Default.ServerRecord)!; await Task.Delay(100, cancellation).ConfigureAwait(false); }
        throw new IOException("Local server did not create an instance record");
    }
    private static bool IsAlive(int pid) { try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } }
    private async Task<ServerRecord> Bootstrap(CancellationToken cancellation)
    {
        var build = Quote(Protocol.Build);
        var package = _options.PackagePath ?? Path.Combine(AppContext.BaseDirectory, "server", "sourcegit-server-" + _runtime + ".tar.gz");
        if (!File.Exists(package)) throw new FileNotFoundException("Matching self-contained Server package is required", package);
        var bytes = await File.ReadAllBytesAsync(package, cancellation).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expected = _options.PackageSha256;
        if (string.IsNullOrEmpty(expected) && File.Exists(package + ".sha256")) expected = (await File.ReadAllTextAsync(package + ".sha256", cancellation).ConfigureAwait(false)).Split(' ', '\r', '\n')[0];
        if (string.IsNullOrWhiteSpace(expected) || !hash.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Server package digest missing or mismatched");
        // The marker makes a redeploy happen when the package content changed under the same build id;
        // a deployment without a marker (older client) always counts as stale.
        var check = "base=\"$HOME/.sourcegit-server\"; version=\"$base/versions/\"" + build + "; test -x \"$version/sourcegit-server\" && [ \"$(cat \"$version/.package-sha256\" 2>/dev/null)\" = " + Quote(hash) + " ] && printf INSTALLED || printf MISSING";
        var redesigned = false;
        if (!(await RunRemote(check, null, cancellation).ConfigureAwait(false)).Contains("INSTALLED", StringComparison.Ordinal))
        {
            SetState("Installing");
            redesigned = true;
            var install = "set -eu; umask 077; base=\"$HOME/.sourcegit-server\"; mkdir -p \"$base/versions\"; chmod 700 \"$base\"; " +
                "archive=\"$base/upload-" + Guid.NewGuid().ToString("N") + ".tar.gz\"; cat > \"$archive\"; " +
                "printf '%s  %s\\n' " + Quote(hash) + " \"$archive\" | sha256sum -c - >/dev/null; " +
                "exec 9>\"$base/install.lock\"; flock 9; version=\"$base/versions/\"" + build + "; " +
                "temp=\"$version.tmp-$$\"; rm -rf \"$temp\"; mkdir -p \"$temp\"; tar -xzf \"$archive\" -C \"$temp\"; chmod 700 \"$temp/sourcegit-server\"; printf '%s\\n' " + Quote(hash) + " > \"$temp/.package-sha256\"; rm -rf \"$version\"; mv \"$temp\" \"$version\"; rm -f \"$archive\"";
            await RunRemote(install, bytes, cancellation).ConfigureAwait(false);
        }
        SetState("Starting");
        // A redeploy replaced the binaries out from under a possibly running (stale) instance; restart it.
        var script = "set -eu; umask 077; base=\"$HOME/.sourcegit-server\"; mkdir -p \"$base\"; chmod 700 \"$base\"; " +
            "exec 9>\"$base/start.lock\"; flock 9; record=\"$base/instance-" + Protocol.Build + ".json\"; version=\"$base/versions/\"" + build + "; " +
            (redesigned ? "if [ -f \"$record\" ]; then pid=$(sed -n 's/.*\"processId\":\\([0-9]*\\).*/\\1/p' \"$record\"); if [ -n \"$pid\" ] && kill -0 \"$pid\" 2>/dev/null; then kill \"$pid\" 2>/dev/null || true; i=0; while kill -0 \"$pid\" 2>/dev/null && [ \"$i\" -lt 50 ]; do sleep .1; i=$((i+1)); done; fi; rm -f \"$record\"; fi; " : "") +
            "if [ -f \"$record\" ]; then pid=$(sed -n 's/.*\"processId\":\\([0-9]*\\).*/\\1/p' \"$record\"); valid=0; " +
            "if [ -n \"$pid\" ] && kill -0 \"$pid\" 2>/dev/null && [ -r \"/proc/$pid/cmdline\" ]; then cmd=$(tr '\\000' ' ' < \"/proc/$pid/cmdline\"); case \"$cmd\" in *\"$version/sourcegit-server\"*\"$record\"*) valid=1;; esac; fi; if [ \"$valid\" -ne 1 ]; then rm -f \"$record\"; fi; fi; " +
            "if [ ! -f \"$record\" ]; then if [ -f \"$base/server-env-setup\" ]; then . \"$base/server-env-setup\" >&2; fi; " +
            "nohup \"$version/sourcegit-server\" --record \"$record\" >\"$base/server-" + Protocol.Build + ".log\" 2>&1 < /dev/null 9>&- & " +
            "i=0; while [ ! -f \"$record\" ] && [ \"$i\" -lt 100 ]; do sleep .1; i=$((i+1)); done; fi; " +
            "if [ ! -f \"$record\" ]; then tail -n 30 \"$base/server-" + Protocol.Build + ".log\" >&2; exit 1; fi; printf 'SOURCEGIT_RECORD '; cat \"$record\"; printf '\\n'";
        var output = await RunRemote(script, null, cancellation).ConfigureAwait(false);
        var line = output.Split('\n').LastOrDefault(x => x.StartsWith("SOURCEGIT_RECORD ", StringComparison.Ordinal)) ?? throw new IOException("Missing Server startup record");
        return JsonSerializer.Deserialize(line[17..], RemoteJson.Default.ServerRecord) ?? throw new IOException("Invalid Server startup record");
    }
    private async Task<string> RunRemote(string script, byte[]? input, CancellationToken cancellation)
    {
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
    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _lifetime.Cancel(); DeleteAskpass(); if (Client != null) { Client.Disconnected -= OnDisconnected; await Client.DisposeAsync().ConfigureAwait(false); }
        if (_tunnel != null) { try { if (!_tunnel.HasExited) _tunnel.Kill(true); } catch (InvalidOperationException) { } _tunnel.Dispose(); }
        // Server continues outstanding work and exits after its configurable idle grace.
        _localServer?.Dispose(); _lifetime.Dispose();
    }
}
