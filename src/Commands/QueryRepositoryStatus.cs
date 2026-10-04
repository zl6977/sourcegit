using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryRepositoryStatus : Command
    {
        [GeneratedRegex(@"\+(\d+) \-(\d+)")]
        private static partial Regex REG_BRANCH_AB();

        public QueryRepositoryStatus(string repo)
        {
            WorkingDirectory = repo;
            RaiseError = false;
        }

        public async Task<Models.RepositoryStatus> GetResultAsync()
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
            {
                try
                {
                    var workspace = SourceGit.Remote.RemoteWorkspace.ForPath(WorkingDirectory);
                    var handle = await workspace.OpenTransientAsync(WorkingDirectory, CancellationToken).ConfigureAwait(false);
                    try
                    {
                        var snapshot = await workspace.Client.GetStatusAsync(handle.Id).ConfigureAwait(false);
                        var remote = new Models.RepositoryStatus();
                        remote.CurrentBranch = snapshot.Branch ?? (string.IsNullOrEmpty(snapshot.HeadOid) || snapshot.HeadOid.Length <= 10 ? "-" : $"({snapshot.HeadOid[..10]})");
                        remote.LocalChanges = snapshot.Changes.Length;
                        remote.Ahead = snapshot.Ahead;
                        remote.Behind = snapshot.Behind;
                        remote.Generation = snapshot.Generation;
                        return remote;
                    }
                    finally { await workspace.ReleaseTransientAsync(handle.Id).ConfigureAwait(false); }
                }
                catch { return null; }
            }

            Args = "status --porcelain=v2 -b -uall --ignore-submodules=dirty";
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return null;

            var status = new Models.RepositoryStatus();
            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var count = lines.Length;
            if (count < 2)
                return null;

            var sha1 = lines[0].Substring(13).Trim(); // Remove "# branch.oid " prefix
            var head = lines[1].Substring(14).Trim(); // Remove "# branch.head " prefix

            if (head.Equals("(detached)", StringComparison.Ordinal))
                status.CurrentBranch = sha1.Length > 10 ? $"({sha1.Substring(0, 10)})" : "-";
            else
                status.CurrentBranch = head;

            var changesCount = 0;
            for (var n = 2; n < lines.Length; n++)
            {
                var line = lines[n];
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                        ParseTrackStatus(status, line.Substring(12).Trim());
                    continue;
                }
                changesCount++;
            }

            status.LocalChanges = changesCount;

            return status;
        }

        private void ParseTrackStatus(Models.RepositoryStatus status, string input)
        {
            var match = REG_BRANCH_AB().Match(input);
            if (match.Success)
            {
                status.Ahead = int.Parse(match.Groups[1].Value);
                status.Behind = int.Parse(match.Groups[2].Value);
            }
        }
    }
}
