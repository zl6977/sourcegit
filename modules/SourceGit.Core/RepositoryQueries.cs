using System.Text;
using System.Text.RegularExpressions;
using SourceGit.Contracts;

namespace SourceGit.Core;

/// <summary>Parses Git state on the repository host; the GUI consumes pure DTOs.</summary>
public sealed partial class RepositoryQueries(RepositoryService repositories)
{
    public async Task<StatusSnapshot> StatusAsync(QueryRequest query, CancellationToken cancellation)
    {
        var args = new[] { "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--ignore-submodules=dirty" };
        var result = await Execute(query, args, cancellation);
        string? headOid = null, branch = null, upstream = null;
        var ahead = 0; var behind = 0;
        var changes = new List<ChangeInfo>();
        var records = Decode(result).Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i].TrimEnd('\r');
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal)) headOid = record[13..].Trim();
            else if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = record[14..].Trim();
                branch = head.Equals("(detached)", StringComparison.Ordinal) ? null : head;
            }
            else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = record[18..].Trim();
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var values = record[12..].Trim().Split(' ');
                if (values.Length == 2 && int.TryParse(values[0].TrimStart('+'), out var a) && int.TryParse(values[1].TrimStart('-'), out var b))
                {
                    ahead = a; behind = b;
                }
            }
            else if (record.Length > 4 && record[1] == ' ')
            {
                var t = record[0];
                if (t == '?')
                {
                    changes.Add(new ChangeInfo(record[2..].TrimEnd('\r'), null, " ", "?", false));
                    continue;
                }
                if (t is not ('1' or '2' or 'u')) continue;
                var x = record[2]; var y = record[3];
                var rest = SkipField(record.AsSpan(5), t == 'u' ? 5 : 4);
                var beforeOids = rest;
                TakeField(rest, out rest);
                if (!IsOidField(TakeField(beforeOids, out _)))
                    rest = beforeOids;
                else
                    for (var n = 1; n < (t == 'u' ? 3 : 2); n++)
                        TakeField(rest, out rest);
                string? original = null;
                if (t == '2')
                {
                    rest = SkipField(rest, 1);
                    if (i + 1 < records.Length) original = records[++i].TrimEnd('\r');
                }
                var path = rest.ToString().TrimEnd('\r');
                if (path.Length == 0) continue;
                var index = x == '.' ? " " : x.ToString();
                var work = y == '.' ? " " : y.ToString();
                var conflict = index + work is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";
                changes.Add(new ChangeInfo(path, original, index, work, conflict));
            }
        }
        return new StatusSnapshot(result.Generation, changes.ToArray(), headOid, branch, upstream, ahead, behind);
    }
    public Task<RefsSnapshot> RefsAsync(QueryRequest query, CancellationToken cancellation)
        => repositories.ReadSnapshotAsync(query.RepositoryId, execute => RefsCoreAsync(query, cancellation, execute), cancellation);

    private async Task<RefsSnapshot> RefsCoreAsync(QueryRequest query, CancellationToken cancellation, Func<GitRequest, Task<GitResult>> execute)
    {
        var result = await Execute(query, ["for-each-ref", "--format=%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(upstream:track)%00%(worktreepath)"], cancellation, execute);
        var detached = false;
        var state = await Execute(query with { NoOptionalLocks = true }, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=no", "--ignore-submodules=dirty"], cancellation, execute);
        foreach (var row in Decode(state).Split('\0'))
        {
            if (row.StartsWith("# branch.head (detached)", StringComparison.Ordinal)) detached = true;
        }
        var detachedOid = detached ? Decode(await Execute(query, ["rev-parse", "--short", "HEAD"], cancellation, execute)).Trim() : null;
        var refs = new List<RefInfo>();
        foreach (var row in Decode(result).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var values = row.TrimEnd('\r').Split('\0');
            if (values.Length != 7) continue;
            ParseTrack(values[5], out var ahead, out var behind);
            refs.Add(new RefInfo(values[0], values[2], values[4], values[5], values[3] == "*", long.TryParse(values[1], out var date) ? date : 0, values[6], ahead, behind));
        }
        return new RefsSnapshot(result.Generation, refs.ToArray(), detachedOid != null, detachedOid);
    }
    private static void ParseTrack(string track, out int ahead, out int behind)
    {
        ahead = 0; behind = 0;
        if (string.IsNullOrEmpty(track)) return;
        var a = TrackAhead().Match(track);
        if (a.Success) ahead = int.Parse(a.Groups[1].Value);
        var b = TrackBehind().Match(track);
        if (b.Success) behind = int.Parse(b.Groups[1].Value);
    }
    public async Task<HistoryPage> HistoryAsync(QueryRequest query, CancellationToken cancellation)
    {
        if (query.Offset < 0 || query.Count < 1 || query.Count > 500) throw new ArgumentOutOfRangeException(nameof(query.Count));
        var args = new List<string> { "log", "-z", "--format=%H%x00%P%x00%an%x00%ae%x00%at%x00%cn%x00%ce%x00%ct%x00%s%x00%B%x00%D", "--skip=" + query.Offset, "--max-count=" + (query.Count + 1) };
        if (!string.IsNullOrEmpty(query.Revision)) { ValidateRevision(query.Revision); args.Add(query.Revision); }
        else args.Add("--all");
        if (query.Path != null) { args.Add("--"); args.Add(query.Path); }
        var result = await Execute(query, args.ToArray(), cancellation);
        var commits = ParseCommits(Decode(result));
        return new HistoryPage(result.Generation, commits.Take(query.Count).ToArray(), commits.Count > query.Count);
    }
    public async Task<CommitInfo> CommitAsync(QueryRequest query, CancellationToken cancellation)
    {
        var revision = query.Revision ?? throw new ArgumentException("Commit revision required");
        ValidateRevision(revision);
        var result = await Execute(query, ["log", "-z", "--max-count=1", "--format=%H%x00%P%x00%an%x00%ae%x00%at%x00%cn%x00%ce%x00%ct%x00%s%x00%B%x00%D", revision, "--"], cancellation);
        return ParseCommits(Decode(result)).FirstOrDefault() ?? throw new KeyNotFoundException("Commit not found");
    }
    [GeneratedRegex(@"^\^?([0-9a-f]+)\s+(.*)\s+\((.*)\s+(\d+)\s+[\-\+]?\d+\s+\d+\) (.*)")]
    private static partial System.Text.RegularExpressions.Regex BlameFormat();
    public async Task<BlameData> BlameAsync(QueryRequest query, CancellationToken cancellation)
    {
        var file = query.Path ?? throw new ArgumentException("Blame file required");
        var revision = query.Revision ?? "HEAD";
        ValidateRevision(revision);
        var args = new List<string> { "blame", "-f", "-t" };
        if (query.IgnoreWhitespace) args.Add("-w");
        args.Add(revision); args.Add("--"); args.Add(file);
        var result = await Execute(query, args.ToArray(), cancellation);
        return ParseBlame(result.Generation, Decode(result));
    }
    private static BlameData ParseBlame(long generation, string raw)
    {
        var lines = new List<BlameLineInfo>();
        var content = new StringBuilder();
        string? last = null;
        var minSha = int.MaxValue; var hasBoundary = false;
        foreach (var row in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (row.Contains('\0')) return new BlameData(generation, true, [], "");
            if (BlameFormat().Match(row) is not { Success: true } match) continue;
            content.Append(match.Groups[5].Value).Append('\n');
            var sha = match.Groups[1].Value;
            lines.Add(new BlameLineInfo(sha, match.Groups[2].Value.Trim(), match.Groups[3].Value, long.Parse(match.Groups[4].Value), sha != last));
            last = sha;
            if (row[0] == '^') { hasBoundary = true; minSha = Math.Min(minSha, sha.Length); }
        }
        if (hasBoundary)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.CommitSHA.Length > minSha) lines[i] = new BlameLineInfo(line.CommitSHA[..minSha], line.File, line.Author, line.Timestamp, line.IsFirstInGroup);
            }
        }
        if (content.Length > 0) content.Length--;
        return new BlameData(generation, false, lines.ToArray(), content.ToString());
    }

    public async Task<DiffInfo> DiffAsync(QueryRequest query, CancellationToken cancellation)
    {
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3" };
        if (query.Revision != null) { ValidateRevision(query.Revision); args.Add(query.Revision); }
        args.Add("--"); if (query.Path != null) args.Add(query.Path);
        var result = await Execute(query, args.ToArray(), cancellation);
        var raw = Decode(result);
        var lines = new List<DiffLine>(); var oldLine = 0; var newLine = 0;
        foreach (var row in raw.Split('\n'))
        {
            var kind = "context";
            if (row.StartsWith("@@", StringComparison.Ordinal))
            {
                kind = "hunk"; var match = HunkHeader().Match(row);
                if (match.Success) { oldLine = int.Parse(match.Groups[1].Value); newLine = int.Parse(match.Groups[2].Value); }
                lines.Add(new DiffLine(kind, row, 0, 0)); continue;
            }
            if (row.StartsWith("diff ", StringComparison.Ordinal) || row.StartsWith("index ", StringComparison.Ordinal) || row.StartsWith("---", StringComparison.Ordinal) || row.StartsWith("+++", StringComparison.Ordinal) || row.StartsWith("new file", StringComparison.Ordinal) || row.StartsWith("deleted file", StringComparison.Ordinal)) kind = "header";
            else if (row.StartsWith('+')) { lines.Add(new DiffLine("add", row, 0, newLine++)); continue; }
            else if (row.StartsWith('-')) { lines.Add(new DiffLine("delete", row, oldLine++, 0)); continue; }
            else if (row.StartsWith(' ')) { lines.Add(new DiffLine(kind, row, oldLine++, newLine++)); continue; }
            lines.Add(new DiffLine(kind, row, 0, 0));
        }
        return new DiffInfo(result.Generation, raw.Contains("Binary files ", StringComparison.Ordinal) || raw.Contains("GIT binary patch", StringComparison.Ordinal), lines.ToArray(), result.OutputBase64);
    }
    private async Task<GitResult> Execute(QueryRequest query, string[] arguments, CancellationToken cancellation, Func<GitRequest, Task<GitResult>>? execute = null)
    {
        var request = new GitRequest(query.RepositoryId, arguments, NoOptionalLocks: query.NoOptionalLocks);
        var result = execute == null ? await repositories.ExecuteAsync(request, cancellation) : await execute(request);
        if (result.ExitCode != 0) throw new IOException(result.Error);
        return result;
    }
    private static List<CommitInfo> ParseCommits(string raw)
    {
        var fields = raw.Split('\0'); var result = new List<CommitInfo>();
        for (var i = 0; i + 10 < fields.Length; i += 11)
        {
            if (fields[i].Length == 0) continue;
            result.Add(new CommitInfo(fields[i], fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[i + 2], fields[i + 3], long.Parse(fields[i + 4]), fields[i + 5], fields[i + 6], long.Parse(fields[i + 7]), fields[i + 8], fields[i + 9], fields[i + 10]));
        }
        return result;
    }
    private static string Decode(GitResult result) => Encoding.UTF8.GetString(Convert.FromBase64String(result.OutputBase64));
    private static ReadOnlySpan<char> SkipField(ReadOnlySpan<char> s, int count)
    {
        for (var n = 0; n < count; n++)
        {
            var p = s.IndexOf(' ');
            if (p < 0) return s;
            s = s[(p + 1)..];
        }
        return s;
    }
    private static string TakeField(ReadOnlySpan<char> s, out ReadOnlySpan<char> rest)
    {
        var p = s.IndexOf(' ');
        if (p < 0) { rest = s[s.Length..]; return s.ToString(); }
        rest = s[(p + 1)..];
        return s[..p].ToString();
    }
    private static bool IsOidField(string s)
    {
        if (s == "-") return true;
        if (s.Length != 40) return false;
        for (var n = 0; n < 40; n++)
        {
            var c = s[n];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
        }
        return true;
    }
    private static void ValidateRevision(string revision) { if (revision.StartsWith('-') || revision.Contains('\0')) throw new ArgumentException("Invalid revision"); }
    [GeneratedRegex("ahead (\\d+)")]
    private static partial Regex TrackAhead();
    [GeneratedRegex("behind (\\d+)")]
    private static partial Regex TrackBehind();
    [GeneratedRegex("^@@ -(\\d+)(?:,\\d+)? \\+(\\d+)(?:,\\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
