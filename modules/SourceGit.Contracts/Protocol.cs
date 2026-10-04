using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceGit.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public const string Build = BuildIdentity.Value;
    public const int MaxFrame = 4 * 1024 * 1024;
    public static JsonElement Null { get; } = JsonDocument.Parse("null").RootElement.Clone();
    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken cancellation = default)
    {
        var header = new byte[4];
        var n = await stream.ReadAsync(header.AsMemory(0, 1), cancellation).ConfigureAwait(false);
        if (n == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellation).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size <= 0 || size > MaxFrame) throw new InvalidDataException("Invalid frame length");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, RemoteJson.Default.Envelope) ?? throw new InvalidDataException("Invalid envelope");
    }
    public static async Task WriteAsync(Stream stream, Envelope message, SemaphoreSlim gate, CancellationToken cancellation = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RemoteJson.Default.Envelope);
        if (bytes.Length > MaxFrame) throw new InvalidDataException("Frame exceeds limit");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await stream.WriteAsync(header, cancellation).ConfigureAwait(false); await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false); await stream.FlushAsync(cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public static JsonElement Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);
}
public sealed record Envelope(string Id, string Method, JsonElement Data, string? Error = null, bool Event = false);
public sealed record HandshakeRequest(string Token, string OwnerId, string RecoveryToken, int Version = Protocol.Version, string Build = Protocol.Build);
public sealed record HandshakeResult(string InstanceId, int Version, string Build, string[] Capabilities);
public sealed record OpenRequest(string Path);
public sealed record RepositoryInfo(string Id, string WorktreePath, string GitDirectory, string CommonDirectory, bool Bare, long Generation);
public sealed record GitRequest(string RepositoryId, string[] Arguments, bool ReadOnly = true, string? OperationId = null, string? InputBase64 = null, string? WorkingDirectory = null, string? Editor = null, string? SSHKey = null, string[]? ConfigOverrides = null, bool StreamOutput = false, bool NoOptionalLocks = false);
public sealed record GitResult(int ExitCode, string OutputBase64, string Error, string? OperationId = null, long Generation = 0);
public sealed record FileRequest(string RepositoryId, string Action, string Path, string? Destination = null, string? ContentBase64 = null, long Offset = 0, int Count = 1024 * 1024, string? ExpectedHash = null, bool Overwrite = false, bool Truncate = false, bool CreateNew = false, string? OperationId = null);
public sealed record FileEntry(string Name, string Path, bool Directory, long Length, string? Hash = null, long ModifiedUtcTicks = 0);
public sealed record FileResult(string? ContentBase64 = null, FileEntry[]? Entries = null, long Length = 0, string? Hash = null, bool Exists = false, bool Directory = false, DateTime LastWriteTimeUtc = default);
public sealed record IdRequest(string Id);
public sealed record OperationInfo(string Id, string State, GitResult? Result = null, string? Error = null, FileResult? FileResult = null);
public sealed record OperationProgress(string OperationId, string Text);
public sealed record OperationOutput(string OperationId, bool StandardError, string ContentBase64);
public sealed record CustomCommandRequest(string RepositoryId, string Command, string OperationId);
public sealed record RepositoryEvent(string RepositoryId, long Generation, string[] Categories);
public sealed record InteractionRequest(string Id, string OperationId, string Kind, string Prompt, string? Content = null);
public sealed record InteractionReply(string Id, bool Accepted, string Value);
public sealed record ServerRecord(int Port, string Token, string InstanceId, int ProcessId, string Build = Protocol.Build);
public sealed record EnvironmentInfo(string OS, string Architecture, string User, string GitVersion, string Home, string[] Capabilities);
public sealed record DirectoryRequest(string Path, int Offset = 0, int Count = 200);
public sealed record AutoFetchRequest(string RepositoryId, int IntervalMinutes, bool Prune = true);
public sealed record QueryRequest(string RepositoryId, string? Revision = null, string? Path = null, int Offset = 0, int Count = 100, bool IgnoreWhitespace = false, bool NoOptionalLocks = false);
public sealed record ChangeInfo(string Path, string? OriginalPath, string IndexStatus, string WorkingTreeStatus, bool Conflict);
public sealed record StatusSnapshot(long Generation, ChangeInfo[] Changes, string? HeadOid = null, string? Branch = null, string? Upstream = null, int Ahead = 0, int Behind = 0);
public sealed record RefInfo(string Name, string ObjectId, string Upstream, string Tracking, bool Head, long CommitterDate = 0, string? WorktreePath = null, int Ahead = 0, int Behind = 0);
public sealed record RefsSnapshot(long Generation, RefInfo[] Refs, bool IsDetachedHead = false, string? DetachedOid = null);
public sealed record CommitInfo(string Id, string[] Parents, string Author, string AuthorEmail, long AuthorTime, string Committer, string CommitterEmail, long CommitTime, string Subject, string Message, string Decorations = "");
public sealed record HistoryPage(long Generation, CommitInfo[] Commits, bool HasMore);
public sealed record DiffLine(string Kind, string Text, int OldLine, int NewLine);
public sealed record DiffInfo(long Generation, bool Binary, DiffLine[] Lines, string RawContentBase64);
public sealed record BlameLineInfo(string CommitSHA, string File, string Author, long Timestamp, bool IsFirstInGroup);
public sealed record BlameData(long Generation, bool IsBinary, BlameLineInfo[] Lines, string Content = "");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(HandshakeRequest))]
[JsonSerializable(typeof(HandshakeResult))]
[JsonSerializable(typeof(OpenRequest))]
[JsonSerializable(typeof(RepositoryInfo))]
[JsonSerializable(typeof(GitRequest))]
[JsonSerializable(typeof(GitResult))]
[JsonSerializable(typeof(FileRequest))]
[JsonSerializable(typeof(FileResult))]
[JsonSerializable(typeof(IdRequest))]
[JsonSerializable(typeof(OperationInfo))]
[JsonSerializable(typeof(OperationProgress))]
[JsonSerializable(typeof(OperationOutput))]
[JsonSerializable(typeof(CustomCommandRequest))]
[JsonSerializable(typeof(RepositoryEvent))]
[JsonSerializable(typeof(InteractionRequest))]
[JsonSerializable(typeof(InteractionReply))]
[JsonSerializable(typeof(ServerRecord))]
[JsonSerializable(typeof(EnvironmentInfo))]
[JsonSerializable(typeof(DirectoryRequest))]
[JsonSerializable(typeof(AutoFetchRequest))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(StatusSnapshot))]
[JsonSerializable(typeof(RefsSnapshot))]
[JsonSerializable(typeof(CommitInfo))]
[JsonSerializable(typeof(HistoryPage))]
[JsonSerializable(typeof(DiffInfo))]
[JsonSerializable(typeof(BlameLineInfo))]
[JsonSerializable(typeof(BlameData))]
[JsonSerializable(typeof(string))]
public partial class RemoteJson : JsonSerializerContext { }
