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
    private readonly ConcurrentDictionary<string, byte> _temporaryFiles = new(StringComparer.Ordinal);
    public event Action<RepositoryEvent>? Changed;
    public event Action<OperationProgress>? Progress;
    public Func<OperationOutput, Task>? Output { get; set; }
    public Func<ProcessStartInfo, string?, Task>? PrepareProcess { get; set; }
    private sealed class Session : IDisposable
    {
        public required RepositoryInfo Info;
        public FileSystemWatcher? Watcher;
        public FileSystemWatcher? MetadataWatcher;
        public Timer? Timer;
        public long Generation;
        public int References = 1;
        public void Dispose() { Watcher?.Dispose(); MetadataWatcher?.Dispose(); Timer?.Dispose(); }
    }
    public async Task<RepositoryInfo> OpenAsync(string path)
    {
        await _sessionGate.WaitAsync();
        try
        {
        path = RemotePaths.Canonical(path);
        var bare = await Query(path, "rev-parse", "--is-bare-repository");
        var git = RemotePaths.Canonical(await Query(path, "rev-parse", "--absolute-git-dir"));
        var common = RemotePaths.Canonical(await Query(path, "rev-parse", "--path-format=absolute", "--git-common-dir"));
        var worktree = bare == "true" ? path : RemotePaths.Canonical(await Query(path, "rev-parse", "--show-toplevel"));
        var existing = _sessions.Values.FirstOrDefault(x => x.Info.WorktreePath == worktree);
        if (existing != null) { existing.References++; return existing.Info with { Generation = existing.Generation }; }
        var info = new RepositoryInfo(Guid.NewGuid().ToString("N"), worktree, git, common, bare == "true", 0);
        var session = new Session { Info = info };
        session.Timer = new Timer(_ => Notify(session, "debounce"), null, Timeout.Infinite, Timeout.Infinite);
        var watcher = new FileSystemWatcher(worktree) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler change = (_, _) => session.Timer?.Change(200, Timeout.Infinite);
        watcher.Changed += change; watcher.Created += change; watcher.Deleted += change;
        watcher.Renamed += (_, _) => session.Timer?.Change(200, Timeout.Infinite);
        watcher.Error += (_, e) => { if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"WATCHER-ERROR worktree={worktree}: {e?.GetException()}"); Notify(session, "error"); };
        watcher.EnableRaisingEvents = true;
        session.Watcher = watcher;
        if (common != Path.Combine(worktree, ".git"))
        {
            var metadata = new FileSystemWatcher(common) { IncludeSubdirectories = true, NotifyFilter = watcher.NotifyFilter };
            metadata.Changed += change; metadata.Created += change; metadata.Deleted += change;
            metadata.Renamed += (_, _) => session.Timer?.Change(200, Timeout.Infinite);
            metadata.Error += (_, e) => { if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"WATCHER-ERROR common={common}: {e?.GetException()}"); Notify(session, "error"); };
            metadata.EnableRaisingEvents = true;
            session.MetadataWatcher = metadata;
        }
        _sessions[info.Id] = session;
        return info;
        }
        finally { _sessionGate.Release(); }
    }
    private void Notify(Session session, string source)
    {
        if (Environment.GetEnvironmentVariable("SOURCEGIT_DEBUG_LOG") == "1") Console.Error.WriteLine($"NOTIFY repo={session.Info.WorktreePath} gen-source={source}");
        var generation = Interlocked.Increment(ref session.Generation);
        Changed?.Invoke(new RepositoryEvent(session.Info.Id, generation, ["workingTree", "refs", "history", "stash"]));
    }
    public void Close(string id)
    {
        _sessionGate.Wait();
        try { if (_sessions.TryGetValue(id, out var session) && --session.References <= 0 && _sessions.TryRemove(id, out _)) session.Dispose(); }
        finally { _sessionGate.Release(); }
    }
    public RepositoryInfo Get(string id) => _sessions.TryGetValue(id, out var value) ? value.Info with { Generation = value.Generation } : throw new KeyNotFoundException("Repository handle expired");
    public RepositoryInfo? FindByCommonDirectory(string common) => _sessions.Values.FirstOrDefault(x => x.Info.CommonDirectory == common)?.Info;
    public async Task<GitResult> ExecuteAsync(GitRequest request, CancellationToken cancellation = default)
    {
        GitPolicy.Validate(request.Arguments, request.ReadOnly);
        var repo = string.IsNullOrEmpty(request.RepositoryId)
            ? new RepositoryInfo("", RemotePaths.Canonical(request.WorkingDirectory ?? throw new ArgumentException("Working directory required")), "", "", false, 0)
            : Get(request.RepositoryId);
        var gate = _locks.GetOrAdd(string.IsNullOrEmpty(repo.CommonDirectory) ? repo.WorktreePath : repo.CommonDirectory, _ => new SemaphoreSlim(1));
        // Queries also synchronize with writes so composite UI results never see a half-finished operation.
        await gate.WaitAsync(cancellation);
        try
        {
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
            var result = await RunAsync(start, request.InputBase64, cancellation, line => { if (request.OperationId != null) Progress?.Invoke(new OperationProgress(request.OperationId, line)); }, request.StreamOutput && request.OperationId != null ? bytes => Output?.Invoke(new OperationOutput(request.OperationId, false, Convert.ToBase64String(bytes.Span))) ?? Task.CompletedTask : null);
            if (!request.ReadOnly && _sessions.TryGetValue(repo.Id, out var session)) Notify(session, "operation");
            return result with { OperationId = request.OperationId, Generation = string.IsNullOrEmpty(repo.Id) ? 0 : Get(repo.Id).Generation };
        }
        finally { gate.Release(); }
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
    public static async Task<GitResult> RunAsync(ProcessStartInfo start, string? input, CancellationToken cancellation, Action<string>? progress = null, Func<ReadOnlyMemory<byte>, Task>? liveOutput = null)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git");
        // Always drain both streams before waiting for exit, including binary stdout.
        using var output = new MemoryStream();
        var stdout = DrainOutput();
        var stderr = DrainError();
        if (input != null) await process.StandardInput.BaseStream.WriteAsync(Convert.FromBase64String(input), cancellation);
        process.StandardInput.Close();
        using var cancel = cancellation.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None);
        await stdout;
        var error = await stderr;
        cancellation.ThrowIfCancellationRequested();
        return new GitResult(process.ExitCode, Convert.ToBase64String(output.ToArray()), error);
        async Task DrainOutput()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer);
                if (read == 0) break;
                if (output.Length + read > 256L * 1024 * 1024) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("Git output exceeds 256 MiB limit; narrow the query"); }
                output.Write(buffer, 0, read);
                if (liveOutput != null) await liveOutput(buffer.AsMemory(0, read));
            }
        }
        async Task<string> DrainError()
        {
            var result = new StringBuilder();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (result.Length < 4 * 1024 * 1024) result.AppendLine(line);
                progress?.Invoke(line);
            }
            return result.ToString();
        }
    }
    public async Task<GitResult> ExecuteCustomAsync(string repositoryId, string command, string operationId, Action<string> progress)
    {
        var repository = Get(repositoryId);
        var gate = _locks.GetOrAdd(repository.CommonDirectory, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { WorkingDirectory = repository.WorktreePath, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c"); start.ArgumentList.Add(command);
            if (PrepareProcess != null) await PrepareProcess(start, operationId);
            var result = await RunAsync(start, null, default, progress, bytes => Output?.Invoke(new OperationOutput(operationId, false, Convert.ToBase64String(bytes.Span))) ?? Task.CompletedTask);
            if (_sessions.TryGetValue(repositoryId, out var session)) Notify(session, "custom");
            return result with { OperationId = operationId, Generation = Get(repositoryId).Generation };
        }
        finally { gate.Release(); }
    }
    private static async Task<string> Query(string path, params string[] args)
    {
        var result = await RunAsync(GitStart(path, args), null, default);
        if (result.ExitCode != 0) throw new IOException(result.Error);
        return Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64)).Trim();
    }
    public async Task<FileResult> FileAsync(FileRequest request)
    {
        if (request.Action is "stat" or "exists" or "read" or "list" or "hash") return await FileCoreAsync(request);
        var full = RemotePaths.Canonical(request.Path);
        var repository = string.IsNullOrEmpty(request.RepositoryId) ? _sessions.Values.FirstOrDefault(x => full.StartsWith(x.Info.WorktreePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))?.Info : Get(request.RepositoryId);
        var gate = _locks.GetOrAdd(repository?.CommonDirectory ?? Path.GetDirectoryName(full)!, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try { return await FileCoreAsync(request); }
        finally { gate.Release(); }
    }
    private async Task<FileResult> FileCoreAsync(FileRequest request)
    {
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
                    await file.ReadExactlyAsync(data);
                    return new FileResult(Convert.ToBase64String(data), Length: file.Length);
                }
            case "write":
                if (request.ContentBase64 == null) throw new ArgumentException("Content required");
                var content = Convert.FromBase64String(request.ContentBase64);
                if (content.Length > 1024 * 1024) throw new ArgumentException("Upload chunk exceeds limit");
                if (request.ExpectedHash != null && File.Exists(path) && Hash(path) != request.ExpectedHash) throw new IOException("File changed since download");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = new FileStream(path, request.CreateNew ? FileMode.CreateNew : request.Truncate ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write))
                { file.Position = request.Offset; await file.WriteAsync(content); }
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
        try { foreach (var session in _sessions.Values) session.Dispose(); _sessions.Clear(); }
        finally { _sessionGate.Release(); }
    }
    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        foreach (var path in _temporaryFiles.Keys)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class GitPolicy
{
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal) { "status", "log", "show", "diff", "diff-files", "diff-index", "ls-files", "ls-tree", "rev-parse", "rev-list", "for-each-ref", "show-ref", "describe", "blame", "cat-file", "check-ignore", "check-attr", "merge-base", "count-objects", "name-rev", "version" };
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal) { "init", "clone", "add", "restore", "reset", "commit", "checkout", "switch", "branch", "tag", "fetch", "pull", "push", "merge", "rebase", "cherry-pick", "revert", "stash", "clean", "rm", "mv", "apply", "am", "format-patch", "archive", "submodule", "worktree", "bisect", "lfs", "flow", "remote", "config", "update-index", "update-ref", "notes", "repack", "gc" };
    public static void Validate(string[] args, bool readOnly)
    {
        if (args.Length == 0 || args.Any(x => x.Contains('\0'))) throw new ArgumentException("Invalid Git arguments");
        var verb = args[0];
        if (!Reads.Contains(verb) && !Writes.Contains(verb) && verb != "reflog") throw new UnauthorizedAccessException("Unsupported Git operation: " + verb);
        if (readOnly && !Reads.Contains(verb) && !IsQuery(args)) throw new UnauthorizedAccessException("Write operation must have an operation ID");
        foreach (var value in args.Skip(1))
            if (value is "--exec" or "-x" or "--ext-diff" or "--textconv" or "--upload-pack" or "--receive-pack" or "--config-env" || value.StartsWith("--exec=", StringComparison.Ordinal) || value.StartsWith("--upload-pack=", StringComparison.Ordinal) || value.StartsWith("--receive-pack=", StringComparison.Ordinal) || value.StartsWith("--config-env=", StringComparison.Ordinal) || value.StartsWith("--config=", StringComparison.Ordinal) || value == "--config" && verb != "config" || value == "-c" && verb is not ("commit" or "config"))
                throw new UnauthorizedAccessException("Executable override is not allowed");
        if (verb is "clone" or "fetch" or "pull" && args.Any(x => x == "-u" || x.StartsWith("-u", StringComparison.Ordinal) && x.Length > 2 || x.StartsWith("--upload-", StringComparison.Ordinal))) throw new UnauthorizedAccessException("Upload-pack overrides require a custom command");
        if (verb == "push" && args.Any(x => x.StartsWith("--receive-", StringComparison.Ordinal))) throw new UnauthorizedAccessException("Receive-pack overrides require a custom command");
        if (readOnly && args.Any(x => x == "--output" || x.StartsWith("--output="))) throw new UnauthorizedAccessException("Output file requires a write operation");
        if (verb == "config")
        {
            if (args.Any(x => x is "--global" or "--system" or "--file" or "-f" || x.StartsWith("--file="))) throw new UnauthorizedAccessException("Only repository config is supported");
            if (!IsQuery(args) && args.Any(IsExecutableConfiguration)) throw new UnauthorizedAccessException("Executable config changes require custom-command capability");
        }
        if (verb == "submodule" && args.Contains("foreach")) throw new UnauthorizedAccessException("Submodule foreach is a custom command");
    }
    private static bool IsExecutableConfiguration(string value)
    {
        var key = value.ToLowerInvariant();
        return key.StartsWith("alias.", StringComparison.Ordinal) || key is "core.editor" or "core.sshcommand" or "core.hookspath" or "core.pager" or "core.gitproxy" or "core.fsmonitor" or "sequence.editor" or "credential.helper" or "diff.external" or "gpg.program" or "include.path" ||
            key.StartsWith("includeif.", StringComparison.Ordinal) && key.EndsWith(".path", StringComparison.Ordinal) ||
            key.StartsWith("credential.", StringComparison.Ordinal) && key.EndsWith(".helper", StringComparison.Ordinal) ||
            key.StartsWith("filter.", StringComparison.Ordinal) && (key.EndsWith(".process", StringComparison.Ordinal) || key.EndsWith(".clean", StringComparison.Ordinal) || key.EndsWith(".smudge", StringComparison.Ordinal)) ||
            key.StartsWith("diff.", StringComparison.Ordinal) && (key.EndsWith(".command", StringComparison.Ordinal) || key.EndsWith(".textconv", StringComparison.Ordinal)) ||
            key.StartsWith("merge.", StringComparison.Ordinal) && key.EndsWith(".driver", StringComparison.Ordinal) ||
            key.StartsWith("gpg.", StringComparison.Ordinal) && key.EndsWith(".program", StringComparison.Ordinal) ||
            key.StartsWith("remote.", StringComparison.Ordinal) && (key.EndsWith(".uploadpack", StringComparison.Ordinal) || key.EndsWith(".receivepack", StringComparison.Ordinal) || key.EndsWith(".proxy", StringComparison.Ordinal));
    }
    public static bool IsQuery(string[] args)
    {
        if (args.Length == 0) return false;
        if (Reads.Contains(args[0])) return !args.Any(x => x == "--output" || x.StartsWith("--output="));
        return args[0] switch
        {
            "reflog" => args.Length == 1 || args[1] is "show" or "exists" || args[1].StartsWith("-"),
            "config" => args.Any(x => x is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l") && !args.Any(x => x is "--add" or "--replace-all" or "--unset" or "--unset-all" or "--remove-section" or "--rename-section" || x.StartsWith("--add=")),
            "branch" => (args.Length == 1 || args.Any(x => x is "--list" or "--show-current" or "-l" or "--all" or "-a" or "--remotes" or "-r")) && !args.Any(x => x is "-D" or "-d" or "--delete" or "-m" or "-M" or "--move" or "-c" or "-C" or "--copy" or "--set-upstream-to" or "--unset-upstream" || x.StartsWith("--set-upstream-to=")),
            "remote" => args.Length == 1 || args[1] is "-v" or "get-url",
            "worktree" => args.Length > 1 && args[1] == "list",
            "stash" => args.Length > 1 && args[1] is "list" or "show",
            "submodule" => args.Length == 1 || args[1] is "status" or "summary",
            "tag" => args.Any(x => x is "--list" or "-l") && !args.Any(x => x is "-d" or "--delete" or "-a" or "-s" or "-f"),
            _ => false
        };
    }
}
