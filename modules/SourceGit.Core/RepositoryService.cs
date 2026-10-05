using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SourceGit.Contracts;

namespace SourceGit.Core;

public sealed class RepositoryService : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionGate = new(1);
    // Live leases only: removing a random, never-reused id makes Close idempotent
    // without an unbounded history of released leases. Protected by _sessionGate.
    private readonly Dictionary<string, (string RepositoryId, string Owner)> _leases = new(StringComparer.Ordinal);
    private readonly int _maxLeases;
    private readonly ConcurrentDictionary<string, byte> _temporaryFiles = new(StringComparer.Ordinal);
    public event Action<RepositoryEvent>? Changed;
    public event Action<OperationProgress>? Progress;
    public Func<OperationOutput, Task>? Output { get; set; }
    public Func<ProcessStartInfo, string?, Task>? PrepareProcess { get; set; }
    internal Action<FileSystemWatcher>? BeforeWatcherEnabled { get; set; }
    public RepositoryService(int maxLeases = 4096)
    {
        if (maxLeases <= 0) throw new ArgumentOutOfRangeException(nameof(maxLeases));
        _maxLeases = maxLeases;
    }
    private sealed class Session : IDisposable
    {
        public required RepositoryInfo Info;
        public FileSystemWatcher? Watcher;
        public FileSystemWatcher? MetadataWatcher;
        public RepositoryChangeDebounce? Debounce;
        public long Generation;
        public long ChangeSequence;
        public int References;
        public void Dispose() { Debounce?.Dispose(); Watcher?.Dispose(); MetadataWatcher?.Dispose(); }
    }
    public async Task<RepositoryInfo> OpenAsync(string path, string owner = "")
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_leases.Count >= _maxLeases) throw new IOException("Repository lease capacity reached; close unused leases before opening more repositories");
            path = RemotePaths.Canonical(path);
            var bare = await Query(path, "rev-parse", "--is-bare-repository");
            var git = RemotePaths.Canonical(await Query(path, "rev-parse", "--absolute-git-dir"));
            var common = RemotePaths.Canonical(await Query(path, "rev-parse", "--path-format=absolute", "--git-common-dir"));
            var worktree = bare == "true" ? path : RemotePaths.Canonical(await Query(path, "rev-parse", "--show-toplevel"));
            var existing = _sessions.Values.FirstOrDefault(x => x.Info.WorktreePath == worktree);
            if (existing != null) return Lease(existing, owner);
            var info = new RepositoryInfo(Guid.NewGuid().ToString("N"), worktree, git, common, bare == "true", 0);
            var session = new Session { Info = info };
            try
            {
                session.Debounce = new RepositoryChangeDebounce(() => Notify(session, "debounce"));
                var watcher = session.Watcher = new FileSystemWatcher();
                watcher.Path = worktree;
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                void ScheduleChange()
                {
                    Interlocked.Increment(ref session.ChangeSequence);
                    session.Debounce.Schedule();
                }
                FileSystemEventHandler change = (_, _) => ScheduleChange();
                watcher.Changed += change; watcher.Created += change; watcher.Deleted += change;
                watcher.Renamed += (_, _) => ScheduleChange();
                watcher.Error += (_, e) => { if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"WATCHER-ERROR worktree={worktree}: {e?.GetException()}"); Interlocked.Increment(ref session.ChangeSequence); session.Debounce.Schedule(urgent: true); };
                BeforeWatcherEnabled?.Invoke(watcher);
                watcher.EnableRaisingEvents = true;
                if (common != Path.Combine(worktree, ".git"))
                {
                    var metadata = session.MetadataWatcher = new FileSystemWatcher();
                    metadata.Path = common;
                    metadata.IncludeSubdirectories = true;
                    metadata.NotifyFilter = watcher.NotifyFilter;
                    metadata.Changed += change; metadata.Created += change; metadata.Deleted += change;
                    metadata.Renamed += (_, _) => ScheduleChange();
                    metadata.Error += (_, e) => { if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"WATCHER-ERROR common={common}: {e?.GetException()}"); Interlocked.Increment(ref session.ChangeSequence); session.Debounce.Schedule(urgent: true); };
                    BeforeWatcherEnabled?.Invoke(metadata);
                    metadata.EnableRaisingEvents = true;
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }
            _sessions[info.Id] = session;
            return Lease(session, owner);
        }
        finally { _sessionGate.Release(); }
    }
    private void Notify(Session session, string source)
    {
        // A queued callback can outlive the last lease. Removed sessions no
        // longer publish events; already admitted consumers may finish.
        if (!_sessions.TryGetValue(session.Info.Id, out var current) || !ReferenceEquals(session, current)) return;
        if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"NOTIFY repo={session.Info.WorktreePath} gen-source={source}");
        var generation = Interlocked.Increment(ref session.Generation);
        Interlocked.Increment(ref session.ChangeSequence);
        Changed?.Invoke(new RepositoryEvent(session.Info.Id, generation, ["workingTree", "refs", "history", "stash"]));
    }
    private RepositoryInfo Lease(Session session, string owner)
    {
        var id = Guid.NewGuid().ToString("N");
        _leases.Add(id, (session.Info.Id, owner));
        session.References++;
        return session.Info with { Generation = session.Generation, LeaseId = id };
    }
    public RepositoryInfo? Close(string leaseId, string owner = "")
    {
        _sessionGate.Wait();
        try
        {
            if (!_leases.TryGetValue(leaseId, out var lease)) return null;
            if (lease.Owner != owner) throw new UnauthorizedAccessException("Repository lease belongs to another owner");
            _leases.Remove(leaseId);
            if (!_sessions.TryGetValue(lease.RepositoryId, out var session)) return null;
            if (--session.References == 0 && _sessions.TryRemove(lease.RepositoryId, out _)) session.Dispose();
            return session.Info;
        }
        finally { _sessionGate.Release(); }
    }
    public RepositoryInfo Get(string id) => _sessions.TryGetValue(id, out var value) ? value.Info with { Generation = value.Generation } : throw new KeyNotFoundException("Repository handle expired");
    public RepositoryInfo? FindByCommonDirectory(string common) => _sessions.Values.FirstOrDefault(x => x.Info.CommonDirectory == common)?.Info;
    public async Task<GitResult> ExecuteAsync(GitRequest request, CancellationToken cancellation = default, Action<Exception>? terminationFailure = null)
    {
        GitCommandPolicy.Validate(request.Arguments, request.ReadOnly);
        var repo = string.IsNullOrEmpty(request.RepositoryId)
            ? new RepositoryInfo("", RemotePaths.Canonical(request.WorkingDirectory ?? throw new ArgumentException("Working directory required")), "", "", false, 0)
            : Get(request.RepositoryId);
        var gate = _locks.GetOrAdd(string.IsNullOrEmpty(repo.CommonDirectory) ? repo.WorktreePath : repo.CommonDirectory, _ => new SemaphoreSlim(1));
        // Individual commands and complete snapshots share the common-directory scheduler.
        await gate.WaitAsync(cancellation);
        try { return await ExecuteCoreAsync(request, repo, cancellation, terminationFailure); }
        finally { gate.Release(); }
    }
    internal async Task<T> ReadSnapshotAsync<T>(string repositoryId, Func<Func<GitRequest, Task<GitResult>>, Task<T>> read, CancellationToken cancellation)
    {
        var repository = Get(repositoryId);
        var gate = _locks.GetOrAdd(repository.CommonDirectory, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellation);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var session = _sessions[repositoryId];
                var sequence = Interlocked.Read(ref session.ChangeSequence);
                var result = await read(request => ExecuteCoreAsync(request, repository, cancellation));
                if (sequence == Interlocked.Read(ref session.ChangeSequence)) return result;
            }
            throw new IOException("Repository changed during the query; refresh and try again");
        }
        finally { gate.Release(); }
    }
    private async Task<GitResult> ExecuteCoreAsync(GitRequest request, RepositoryInfo repo, CancellationToken cancellation, Action<Exception>? terminationFailure = null)
    {
        GitCommandPolicy.Validate(request.Arguments, request.ReadOnly);
        var directory = request.WorkingDirectory == null ? repo.WorktreePath : RemotePaths.Canonical(request.WorkingDirectory);
        if (!string.IsNullOrEmpty(repo.Id) && directory != repo.WorktreePath)
            throw new ArgumentException("The operation working directory must match the opened repository");
        var start = GitStart(directory, request.Arguments);
        if (request.NoOptionalLocks) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var value in request.ConfigOverrides ?? [])
        {
            var equals = value.IndexOf('=');
            var key = equals < 0 ? value : value[..equals];
            // gpg.ssh.allowedSignersFile=/dev/null pins the GUI's commit-signinfo query: without an explicit
            // signers file git attempts network key retrieval for SSH signatures; the pinned empty file keeps
            // verification local-only, matching the command's local-repository behavior.
            if (value.Contains('\0') || key is not ("core.untrackedCache" or "status.showUntrackedFiles" or "core.commentChar" or "core.commentString" or "protocol.file.allow" or "gpg.ssh.allowedSignersFile"))
                throw new UnauthorizedAccessException("Unsupported command config override");
            if (key == "core.untrackedCache" && value != "core.untrackedCache=true" || key == "status.showUntrackedFiles" && value is not ("status.showUntrackedFiles=all" or "status.showUntrackedFiles=no" or "status.showUntrackedFiles=normal") || key == "protocol.file.allow" && value != "protocol.file.allow=always" || key == "gpg.ssh.allowedSignersFile" && value != "gpg.ssh.allowedSignersFile=/dev/null")
                throw new UnauthorizedAccessException("Unsupported command config value");
            start.ArgumentList.Insert(0, value);
            start.ArgumentList.Insert(0, "-c");
        }
        if (PrepareProcess != null) await PrepareProcess(start, request.OperationId);
        if (!string.IsNullOrEmpty(request.SSHKey))
            start.Environment["GIT_SSH_COMMAND"] = "ssh -i " + ShellQuote(Path.GetFullPath(request.SSHKey));
        if (request.Editor == "None") { start.Environment["GIT_EDITOR"] = "true"; start.Environment["GIT_SEQUENCE_EDITOR"] = "true"; }
        GitResult result;
        try
        {
            result = await RunAsync(start, request.InputBase64, cancellation, line => { if (request.OperationId != null) Progress?.Invoke(new OperationProgress(request.OperationId, line)); }, request.StreamOutput && request.OperationId != null ? bytes => Output?.Invoke(new OperationOutput(request.OperationId, false, Convert.ToBase64String(bytes.Span))) ?? Task.CompletedTask : null, terminationFailure);
        }
        finally { if (!request.ReadOnly && _sessions.TryGetValue(repo.Id, out var session)) Notify(session, "operation"); }
        // Closing the last lease during a persistent write cannot change its outcome.
        return result with { OperationId = request.OperationId, Generation = _sessions.TryGetValue(repo.Id, out var completed) ? completed.Generation : repo.Generation };
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public static ProcessStartInfo GitStart(string path, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--no-pager"); start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["LC_ALL"] = "C.UTF-8";
        return start;
    }
    public static async Task<GitResult> RunAsync(ProcessStartInfo start, string? input, CancellationToken cancellation, Action<string>? progress = null, Func<ReadOnlyMemory<byte>, Task>? liveOutput = null, Action<Exception>? terminationFailure = null, Action<Process>? terminate = null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(ProcessLifetime.Prepare(start)) ?? throw new IOException("Could not start Git");
        ProcessLifetime? lifetime = null;
        Exception? failure = null;
        var outcome = 0; // 0 pending, 1 fully completed, 2 termination owns cleanup
        var outcomeGate = new object();
        // Always drain both streams before waiting for exit, including binary stdout.
        using var output = new MemoryStream();
        Task? stdout = null;
        Task<string>? stderr = null;
        var inputClosed = false;
        stdout = DrainOutput();
        stderr = DrainError();
        try { lifetime = ProcessLifetime.Attach(process); }
        catch (Exception error) { Stop(error); }
        using var cancel = cancellation.Register(() => Stop(null)); // before any possibly blocked stdin write
        try
        {
            if (input != null) await process.StandardInput.BaseStream.WriteAsync(Convert.FromBase64String(input), cancellation);
        }
        catch (Exception error) { Stop(error); }
        finally { try { process.StandardInput.Close(); } catch (IOException) { } lock (outcomeGate) inputClosed = true; }
        await process.WaitForExitAsync(CancellationToken.None);
        await stdout;
        var errorText = await stderr;
        // Even a successful parent may leave descendants with closed pipes.
        // Keep containment and the execution deadline until the whole tree exits.
        if (lifetime != null)
        {
            // A failed kill never permits an early lock release. Wait until the
            // entire contained tree actually exits, reporting cleanup failures.
            var cleanupRetry = 500;
            while (true)
            {
                try { await lifetime.WaitForCleanupAsync(); break; }
                catch (Exception cleanupError)
                {
                    terminationFailure?.Invoke(cleanupError);
                    await Task.Delay(cleanupRetry);
                    cleanupRetry = Math.Min(5000, cleanupRetry * 2);
                }
            }
        }
        lock (outcomeGate) { if (outcome == 0) outcome = 1; }
        cancel.Dispose(); // wait for a concurrent kill before closing its job handle
        lifetime?.Dispose();
        if (failure != null && failure is not OperationCanceledException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (outcome == 2 && cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return new GitResult(process.ExitCode, Convert.ToBase64String(output.ToArray()), errorText);
        void Stop(Exception? reason)
        {
            lock (outcomeGate)
            {
                if (reason != null) failure ??= reason;
                if (outcome == 1) return;
                // The work may have finished just before its timer callback ran.
                // Confirm all cleanup under the same gate before choosing a kill.
                if (reason == null && failure == null && inputClosed && process.HasExited && stdout?.IsCompletedSuccessfully == true && stderr?.IsCompletedSuccessfully == true && lifetime != null)
                {
                    try { if (lifetime.IsEmpty) { outcome = 1; return; } }
                    catch (Exception cleanupError) { terminationFailure?.Invoke(cleanupError); }
                }
                outcome = 2;
                try
                {
                    if (terminate != null) terminate(process);
                    else if (lifetime != null) lifetime.Terminate();
                    else if (!process.HasExited) process.Kill(true);
                }
                catch (Exception killError) { terminationFailure?.Invoke(killError); }
            }
        }
        async Task DrainOutput()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer);
                    if (read == 0) break;
                    if (failure != null) continue; // discard remaining bytes while reaping
                    if (output.Length + read > 256L * 1024 * 1024) { Stop(new IOException("Git output exceeds 256 MiB limit; narrow the query")); continue; }
                    output.Write(buffer, 0, read);
                    if (liveOutput != null) try { await liveOutput(buffer.AsMemory(0, read)); } catch (Exception callbackError) { Stop(callbackError); }
                }
            }
            catch (Exception readError) { Stop(readError); }
        }
        async Task<string> DrainError()
        {
            var result = new StringBuilder();
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (result.Length < 4 * 1024 * 1024) result.AppendLine(line);
                    if (failure == null) try { progress?.Invoke(line); } catch (Exception callbackError) { Stop(callbackError); }
                }
            }
            catch (Exception readError) { Stop(readError); }
            return result.ToString();
        }
    }
    public async Task<GitResult> ExecuteCustomAsync(string repositoryId, string command, string operationId, Action<string> progress, CancellationToken cancellation = default, Action<Exception>? terminationFailure = null)
    {
        var repository = Get(repositoryId);
        var gate = _locks.GetOrAdd(repository.CommonDirectory, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellation);
        GitResult result;
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { WorkingDirectory = repository.WorktreePath, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c"); start.ArgumentList.Add(command);
            if (PrepareProcess != null) await PrepareProcess(start, operationId);
            result = await RunAsync(start, null, cancellation, progress, bytes => Output?.Invoke(new OperationOutput(operationId, false, Convert.ToBase64String(bytes.Span))) ?? Task.CompletedTask, terminationFailure);
        }
        finally { try { if (_sessions.TryGetValue(repositoryId, out var session)) Notify(session, "custom"); } finally { gate.Release(); } }
        return result with { OperationId = operationId, Generation = _sessions.TryGetValue(repositoryId, out var completed) ? completed.Generation : repository.Generation };
    }
    private static async Task<string> Query(string path, params string[] args)
    {
        var result = await RunAsync(GitStart(path, args), null, default);
        if (result.ExitCode != 0) throw new IOException(result.Error);
        return Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)).Trim();
    }
    public async Task<FileResult> FileAsync(FileRequest request, CancellationToken cancellation = default)
    {
        if (request.Action is "stat" or "exists" or "read" or "list" or "hash") return await FileCoreAsync(request, cancellation);
        var full = RemotePaths.Canonical(request.Path);
        var repository = string.IsNullOrEmpty(request.RepositoryId) ? _sessions.Values.FirstOrDefault(x => full.StartsWith(x.Info.WorktreePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))?.Info : Get(request.RepositoryId);
        var gate = _locks.GetOrAdd(repository?.CommonDirectory ?? Path.GetDirectoryName(full)!, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellation);
        try { return await FileCoreAsync(request, cancellation); }
        finally { try { if (repository != null && _sessions.TryGetValue(repository.Id, out var session)) Notify(session, "file"); } finally { gate.Release(); } }
    }
    private async Task<FileResult> FileCoreAsync(FileRequest request, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var repo = string.IsNullOrEmpty(request.RepositoryId) ? null : Get(request.RepositoryId);
        var path = repo == null ? RemotePaths.Canonical(request.Path) : ResolvePath(repo, request.Path);
        switch (request.Action)
        {
            case "tempfile":
                var temporary = Path.Combine(Path.GetTempPath(), "sourcegit-" + Guid.NewGuid().ToString("N"));
                using (File.Open(temporary, FileMode.CreateNew, FileAccess.Write)) { }
                _temporaryFiles[temporary] = 0;
                return new FileResult(Entries: [Entry(temporary)], Exists: true);
            case "stat":
                return new FileResult(Length: File.Exists(path) ? new FileInfo(path).Length : 0, Entries: [Entry(path)], Exists: File.Exists(path) || Directory.Exists(path), Directory: Directory.Exists(path), LastWriteTimeUtc: File.Exists(path) || Directory.Exists(path) ? File.GetLastWriteTimeUtc(path) : default);
            case "exists":
                return new FileResult(Length: File.Exists(path) || Directory.Exists(path) ? 1 : 0, Exists: File.Exists(path) || Directory.Exists(path), Directory: Directory.Exists(path));
            case "read":
                if (request.Count < 0 || request.Count > 1024 * 1024 || request.Offset < 0) throw new ArgumentOutOfRangeException(nameof(request.Count));
                await using (var file = File.OpenRead(path))
                {
                    file.Position = request.Offset;
                    var data = new byte[Math.Min(request.Count, Math.Max(0, file.Length - request.Offset))];
                    await file.ReadExactlyAsync(data, cancellation);
                    return new FileResult(Convert.ToBase64String(data), Length: file.Length);
                }
            case "write":
                if (request.ContentBase64 == null) throw new ArgumentException("Content required");
                var content = Convert.FromBase64String(request.ContentBase64);
                if (content.Length > 1024 * 1024) throw new ArgumentException("Upload chunk exceeds limit");
                if (request.ExpectedHash != null && File.Exists(path) && Hash(path) != request.ExpectedHash) throw new IOException("File changed since download");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = new FileStream(path, request.CreateNew ? FileMode.CreateNew : request.Truncate ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write))
                { file.Position = request.Offset; await file.WriteAsync(content, cancellation); }
                return new FileResult(Length: new FileInfo(path).Length);
            case "hash": return new FileResult(Hash: Hash(path), Length: new FileInfo(path).Length);
            case "replace":
                if (request.ExpectedHash == null) throw new ArgumentException("Replacement requires an expected target hash");
                var staged = RemotePaths.Canonical(request.Destination ?? throw new ArgumentException("Staged upload required"));
                if (!_temporaryFiles.ContainsKey(staged) || !File.Exists(staged)) throw new UnauthorizedAccessException("Replacement source is not a server-managed temporary upload");
                var sibling = Path.Combine(Path.GetDirectoryName(path)!, ".sourcegit-replace-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ValidateExpectedHash();
                    File.Copy(staged, sibling, false);
                    if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(sibling, File.GetUnixFileMode(path));
                    ValidateExpectedHash();
                    File.Move(sibling, path, true);
                    return new FileResult(Length: new FileInfo(path).Length, Hash: Hash(path));
                }
                finally { if (File.Exists(sibling)) File.Delete(sibling); }
                void ValidateExpectedHash()
                {
                    var actual = File.Exists(path) ? Hash(path) : "";
                    if (!string.Equals(actual, request.ExpectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Target changed since download; refresh before replacing it");
                }
            case "list":
                return new FileResult(Entries: Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal).Skip((int)request.Offset).Take(Math.Clamp(request.Count, 1, 1000)).Select(Entry).ToArray());
            case "mkdir": Directory.CreateDirectory(path); break;
            case "delete": UnreadOnly(path); File.Delete(path); break;
            case "deleteDirectory": UnreadOnly(path); Directory.Delete(path, false); break;
            case "deleteTree": UnreadOnlyTree(path); Directory.Delete(path, true); break;
            case "truncate": await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write)) file.SetLength(request.Offset); break;
            case "move":
                var destinationPath = request.Destination ?? throw new ArgumentException("Destination required");
                var destination = repo == null ? RemotePaths.Canonical(destinationPath) : ResolvePath(repo, destinationPath);
                if (Directory.Exists(path)) Directory.Move(path, destination); else File.Move(path, destination, request.Overwrite); break;
            default: throw new ArgumentException("Unknown file operation");
        }
        return new FileResult();
    }
    // Windows DeleteFile refuses read-only items (git loose objects and other VCS artifacts are commonly created read-only); clear the bit before deletion.
    private static void UnreadOnly(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path) && !Directory.Exists(path)) return;
        try { var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void UnreadOnlyTree(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path)) return;
        foreach (var item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) UnreadOnly(item);
    }
    public static FileEntry Entry(string path)
    {
        var directory = Directory.Exists(path);
        return new FileEntry(Path.GetFileName(path), path, directory, !directory && File.Exists(path) ? new FileInfo(path).Length : 0, ModifiedUtcTicks: File.Exists(path) || directory ? File.GetLastWriteTimeUtc(path).Ticks : 0);
    }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string ResolvePath(RepositoryInfo repo, string path)
    {
        var full = RemotePaths.Canonical(Path.IsPathRooted(path) ? path : Path.Combine(repo.WorktreePath, path));
        // Repository metadata may live outside a linked worktree; all other absolute locations are rejected.
        if (!new[] { repo.WorktreePath, repo.GitDirectory, repo.CommonDirectory }.Any(root => full == root || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Path is outside repository");
        return full;
    }
    public void CloseAll()
    {
        _sessionGate.Wait();
        try { foreach (var session in _sessions.Values) session.Dispose(); _sessions.Clear(); _leases.Clear(); }
        finally { _sessionGate.Release(); }
    }
    public void Dispose()
    {
        CloseAll();
        foreach (var path in _temporaryFiles.Keys)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
