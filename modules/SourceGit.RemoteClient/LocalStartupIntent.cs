using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

internal sealed record LocalStartupIntent(string Nonce, string RecordPath, int OwnerPid, long OwnerStart, int ChildPid = 0, long ChildStart = 0);
[JsonSerializable(typeof(LocalStartupIntent))]
internal partial class LocalStartupIntentJson : JsonSerializerContext { }

// This marker closes the gap where a GUI's OS lock is gone but its child has
// not published a record. It contains identities, never connection credentials.
internal sealed class LocalStartupIntentFile(string recordPath)
{
    private string Path => recordPath + ".startup.pending";
    internal async Task<LocalStartupIntent?> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > 16 * 1024) throw new IOException("Local startup intent is oversized; preserved: " + Path);
            var intent = await JsonSerializer.DeserializeAsync(input, LocalStartupIntentJson.Default.LocalStartupIntent, cancellation).ConfigureAwait(false);
            if (intent == null || !Guid.TryParseExact(intent.Nonce, "N", out _) || intent.RecordPath != recordPath || intent.OwnerPid <= 0 || intent.OwnerStart <= 0 ||
                intent.ChildPid < 0 || (intent.ChildPid == 0 ? intent.ChildStart != 0 : intent.ChildStart <= 0))
                throw new IOException("Invalid local startup intent; preserved: " + Path);
            return intent;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException error) { throw new IOException("Unreadable local startup intent; preserved: " + Path, error); }
    }
    internal async Task<LocalStartupIntent> BeginAsync(CancellationToken cancellation)
    {
        using var owner = Process.GetCurrentProcess();
        var intent = new LocalStartupIntent(Guid.NewGuid().ToString("N"), recordPath, owner.Id, owner.StartTime.ToUniversalTime().Ticks);
        await WriteAsync(intent, false, cancellation).ConfigureAwait(false);
        return intent;
    }
    internal async Task<LocalStartupIntent> BindAsync(LocalStartupIntent intent, Process child)
    {
        _ = child.SafeHandle;
        var bound = intent with { ChildPid = child.Id, ChildStart = child.StartTime.ToUniversalTime().Ticks };
        if (await ReadAsync(default).ConfigureAwait(false) != intent) throw new IOException("Local startup intent changed; preserved: " + Path);
        await WriteAsync(bound, true, CancellationToken.None).ConfigureAwait(false);
        return bound;
    }
    internal async Task RemoveAsync(LocalStartupIntent intent)
    {
        if (await ReadAsync(default).ConfigureAwait(false) == intent) File.Delete(Path);
    }
    private async Task WriteAsync(LocalStartupIntent intent, bool replace, CancellationToken cancellation)
    {
        var temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, intent, LocalStartupIntentJson.Default.LocalStartupIntent, cancellation).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, Path, replace);
        }
        finally { File.Delete(temporary); }
    }
    internal static Process? ObserveChild(LocalStartupIntent intent)
    {
        if (intent.ChildPid == 0) throw new IOException("Local startup child identity is unknown; startup intent preserved. Check the running server before manual recovery: " + intent.RecordPath);
        Process? child = null;
        try
        {
            child = Process.GetProcessById(intent.ChildPid);
            _ = child.SafeHandle;
            if (child.HasExited || child.StartTime.ToUniversalTime().Ticks != intent.ChildStart) { child.Dispose(); return null; }
            return child;
        }
        catch (ArgumentException) { child?.Dispose(); return null; }
        catch { child?.Dispose(); throw; }
    }
    internal async Task<ServerRecord?> WaitForRecordAsync(LocalStartupIntent intent, Func<CancellationToken, Task<ServerRecord?>> readRecord, TimeSpan timeout, CancellationToken cancellation)
    {
        using var child = ObserveChild(intent);
        if (child == null) { await RemoveAsync(intent).ConfigureAwait(false); return null; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        using var observer = new RecordPublicationObserver(recordPath);
        var exit = child.WaitForExitAsync(deadline.Token);
        try
        {
            var record = await observer.WaitAsync(readRecord, exit, deadline.Token).ConfigureAwait(false);
            if (record != null)
            {
                if (record.ProcessId != intent.ChildPid) throw new IOException("Local pending startup record belongs to another PID; intent and record preserved: " + recordPath);
                return record; // Caller still performs its authenticated probe.
            }
            await RemoveAsync(intent).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new IOException($"Local startup PID {intent.ChildPid} is still pending after {timeout.TotalSeconds:g} seconds; intent preserved and no successor started: " + recordPath, error); }
        finally { deadline.Cancel(); }
    }
}
