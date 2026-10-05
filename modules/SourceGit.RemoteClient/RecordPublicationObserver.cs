using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

// File events wake an authoritative read. Register first, then check the file;
// a signaled generation stays latched until the next read begins.
internal sealed class RecordPublicationObserver : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    internal Action? BeforeRead { get; set; }
    internal Action? BeforeWait { get; set; }
    internal int Reads { get; private set; }
    internal RecordPublicationObserver(string recordPath)
    {
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(recordPath)!, Path.GetFileName(recordPath))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, _) => Notify(); _watcher.Changed += (_, _) => Notify(); _watcher.Deleted += (_, _) => Notify(); _watcher.Renamed += (_, _) => Notify();
        _watcher.Error += (_, _) => NotifyError();
        try { _watcher.EnableRaisingEvents = true; }
        catch { _watcher.Dispose(); throw; }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Notify() { lock (_gate) _changed.TrySetResult(); }
    internal void NotifyError() => Notify(); // The same one-reread path for real overflow and injected faults.
    internal async Task<ServerRecord?> WaitAsync(Func<CancellationToken, Task<ServerRecord?>> readRecord, Task? exit, CancellationToken cancellation)
    {
        while (true)
        {
            Task notification;
            lock (_gate) { if (_changed.Task.IsCompleted) _changed = NewSignal(); notification = _changed.Task; }
            BeforeRead?.Invoke();
            Reads++;
            var record = await readRecord(cancellation).ConfigureAwait(false);
            if (record != null) return record;
            if (exit?.IsCompletedSuccessfully == true) return null;
            BeforeWait?.Invoke();
            if (exit == null) await notification.WaitAsync(cancellation).ConfigureAwait(false);
            else await Task.WhenAny(notification, exit).WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
    }
    public void Dispose() => _watcher.Dispose();
}
