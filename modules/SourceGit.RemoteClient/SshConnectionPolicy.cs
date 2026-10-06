using System.Collections.Concurrent;

namespace SourceGit.RemoteClient;

public sealed class SshConnectionException : IOException
{
    public bool Retryable { get; }
    public int ExitCode { get; }
    internal SshConnectionException(string message, bool retryable = false, int exitCode = -1) : base(message)
    { Retryable = retryable; ExitCode = exitCode; }

    internal static SshConnectionException FromExit(int code, string stderr)
    {
        // Unknown failures are terminal too: do not guess that another authentication is safe.
        var terminal = new[] { "permission denied", "authentication failed", "too many authentication failures", "no supported authentication", "host key verification failed", "remote host identification has changed", "bad configuration", "bad owner or permissions", "invalid format" };
        var network = new[] { "connection timed out", "connection refused", "network is unreachable", "no route to host", "could not resolve hostname", "connection reset by peer" };
        var retryable = code == 255 && !terminal.Any(x => stderr.Contains(x, StringComparison.OrdinalIgnoreCase)) && network.Any(x => stderr.Contains(x, StringComparison.OrdinalIgnoreCase));
        return new SshConnectionException("SSH failed: " + stderr.Trim(), retryable, code);
    }
}

internal sealed class SshConnectionPolicy
{
    private static readonly ConcurrentDictionary<string, SshConnectionPolicy> Hosts = new(StringComparer.OrdinalIgnoreCase);
    internal static SshConnectionPolicy ForHost(string target) => Hosts.GetOrAdd(target.Trim(), _ => new());
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Queue<long> _attempts = new();
    private readonly TimeProvider _clock;
    private Exception? _failure;
    internal SshConnectionPolicy(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    internal async Task<IDisposable> EnterAsync(bool manual, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var now = _clock.GetTimestamp();
            while (_attempts.TryPeek(out var oldest) && _clock.GetElapsedTime(oldest, now) >= TimeSpan.FromMinutes(10)) _attempts.Dequeue();
            if (!manual && _failure != null)
                throw new SshConnectionException("Automatic SSH connection stopped: " + _failure.Message + "; use Connect after checking credentials and host configuration.");
            if (_attempts.Count >= 3)
                throw new SshConnectionException("SSH connection limit reached (3 attempts per host in 10 minutes); wait before reconnecting.");
            if (_attempts.Count > 0)
            {
                var delay = TimeSpan.FromSeconds(30 * Math.Pow(2, _attempts.Count - 1) + Random.Shared.NextDouble() * 5);
                var remaining = delay - _clock.GetElapsedTime(_attempts.Last(), now);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _clock, cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            _attempts.Enqueue(_clock.GetTimestamp());
            if (manual) _failure = null;
            return new Release(_gate);
        }
        catch { _gate.Release(); throw; }
    }

    internal void Failed(Exception error)
    {
        if (error is not SshConnectionException { Retryable: true }) _failure = error;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
