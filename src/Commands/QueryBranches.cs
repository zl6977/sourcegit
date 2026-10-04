using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryBranches : Command
    {
        private const string PREFIX_LOCAL = "refs/heads/";
        private const string PREFIX_REMOTE = "refs/remotes/";
        private const string PREFIX_DETACHED_AT = "(HEAD detached at";
        private const string PREFIX_DETACHED_FROM = "(HEAD detached from";
        public long Generation { get; private set; }

        public QueryBranches(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "branch -l --all -v --format=\"%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(upstream:trackshort)%00%(worktreepath)\"";
        }

        public async Task<List<Models.Branch>> GetResultAsync()
        {
            var branches = new List<Models.Branch>();
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
            {
                try
                {
                    var workspace = SourceGit.Remote.RemoteWorkspace.ForPath(WorkingDirectory);
                    var handle = await workspace.OpenTransientAsync(WorkingDirectory, CancellationToken).ConfigureAwait(false);
                    try
                    {
                        var snapshot = await workspace.Client.GetRefsAsync(handle.Id, CancellationToken).ConfigureAwait(false);
                        Generation = snapshot.Generation;
                        var remoteRefs = new Dictionary<string, Models.Branch>();
                        foreach (var info in snapshot.Refs)
                        {
                            var b = MapBranch(info);
                            if (b != null)
                            {
                                branches.Add(b);
                                if (!b.IsLocal) remoteRefs.Add(b.FullName, b);
                            }
                        }
                        if (snapshot.IsDetachedHead)
                        {
                            var pseudoName = "(HEAD detached at " + snapshot.DetachedOid + ")";
                            branches.Insert(0, new Models.Branch() { Name = pseudoName, FullName = pseudoName, IsDetachedHead = true, IsCurrent = true, Head = snapshot.DetachedOid });
                        }
                        foreach (var b in branches)
                        {
                            if (b.IsLocal && !string.IsNullOrEmpty(b.Upstream) && !remoteRefs.ContainsKey(b.Upstream))
                                b.IsUpstreamGone = true;
                        }
                        return branches;
                    }
                    finally { await workspace.ReleaseTransientAsync(handle.Id).ConfigureAwait(false); }
                }
                catch { return branches; }
            }
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return branches;

            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var mismatched = new HashSet<string>();
            var remotes = new Dictionary<string, Models.Branch>();
            foreach (var line in lines)
            {
                var b = ParseLine(line, mismatched);
                if (b != null)
                {
                    branches.Add(b);
                    if (!b.IsLocal)
                        remotes.Add(b.FullName, b);
                }
            }

            foreach (var b in branches)
            {
                if (b.IsLocal && !string.IsNullOrEmpty(b.Upstream))
                {
                    if (remotes.TryGetValue(b.Upstream, out var upstream))
                    {
                        b.IsUpstreamGone = false;

                        if (mismatched.Contains(b.FullName))
                            await new QueryTrackStatus(WorkingDirectory).GetResultAsync(b, upstream).ConfigureAwait(false);
                    }
                    else
                    {
                        b.IsUpstreamGone = true;
                    }
                }
            }

            return branches;
        }
        private static Models.Branch MapBranch(SourceGit.Contracts.RefInfo info)
        {
            var refName = info.Name;
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                return null;

            var branch = new Models.Branch
            {
                FullName = refName,
                CommitterDate = (ulong)info.CommitterDate,
                Head = info.ObjectId,
                IsCurrent = info.Head,
                Upstream = info.Upstream,
                IsUpstreamGone = false,
                WorktreePath = info.WorktreePath ?? string.Empty,
                Ahead = new List<string>(new string[info.Ahead]),
                Behind = new List<string>(new string[info.Behind]),
            };

            if (refName.StartsWith(PREFIX_LOCAL, StringComparison.Ordinal))
            {
                branch.Name = refName.Substring(PREFIX_LOCAL.Length);
                branch.IsLocal = true;
            }
            else if (refName.StartsWith(PREFIX_REMOTE, StringComparison.Ordinal))
            {
                var name = refName.Substring(PREFIX_REMOTE.Length);
                var nameParts = name.Split('/', 2);
                if (nameParts.Length != 2)
                    return null;

                branch.Remote = nameParts[0];
                branch.Name = nameParts[1];
                branch.IsLocal = false;
            }
            else
            {
                branch.Name = refName;
                branch.IsLocal = true;
            }

            return branch;
        }

        private Models.Branch ParseLine(string line, HashSet<string> mismatched)
        {
            var parts = line.Split('\0');
            if (parts.Length != 7)
                return null;

            var branch = new Models.Branch();
            var refName = parts[0];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                return null;

            branch.IsDetachedHead = refName.StartsWith(PREFIX_DETACHED_AT, StringComparison.Ordinal) ||
                refName.StartsWith(PREFIX_DETACHED_FROM, StringComparison.Ordinal);

            if (refName.StartsWith(PREFIX_LOCAL, StringComparison.Ordinal))
            {
                branch.Name = refName.Substring(PREFIX_LOCAL.Length);
                branch.IsLocal = true;
            }
            else if (refName.StartsWith(PREFIX_REMOTE, StringComparison.Ordinal))
            {
                var name = refName.Substring(PREFIX_REMOTE.Length);
                var nameParts = name.Split('/', 2);
                if (nameParts.Length != 2)
                    return null;

                branch.Remote = nameParts[0];
                branch.Name = nameParts[1];
                branch.IsLocal = false;
            }
            else
            {
                branch.Name = refName;
                branch.IsLocal = true;
            }

            ulong.TryParse(parts[1], out var committerDate);

            branch.FullName = refName;
            branch.CommitterDate = committerDate;
            branch.Head = parts[2];
            branch.IsCurrent = parts[3] == "*";
            branch.Upstream = parts[4];
            branch.IsUpstreamGone = false;

            if (branch.IsLocal &&
                !string.IsNullOrEmpty(branch.Upstream) &&
                !string.IsNullOrEmpty(parts[5]) &&
                !parts[5].Equals("=", StringComparison.Ordinal))
                mismatched.Add(branch.FullName);

            branch.WorktreePath = parts[6];
            return branch;
        }
    }
}
