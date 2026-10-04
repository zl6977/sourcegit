using Process = SourceGit.Remote.GitProcess;
﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryLocalChanges : Command
    {
        [GeneratedRegex(@"^(\s?[\w\?]{1,4})\s+(.+)$")]
        private static partial Regex REG_FORMAT();
        private readonly bool _includeUntracked;
        private readonly bool _noOptionalLocks;

        public QueryLocalChanges(string repo, bool includeUntracked = true, bool noOptionalLocks = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            _includeUntracked = includeUntracked;
            _noOptionalLocks = noOptionalLocks;

            var builder = new StringBuilder();
            if (noOptionalLocks)
                builder.Append("--no-optional-locks ");
            if (includeUntracked)
                builder.Append("-c core.untrackedCache=true -c status.showUntrackedFiles=all status -uall --ignore-submodules=dirty --porcelain");
            else
                builder.Append("status -uno --ignore-submodules=dirty --porcelain");

            Args = builder.ToString();
        }
        public long Generation { get; private set; }

        public async Task<List<Models.Change>> GetResultAsync()
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
            {
                try
                {
                    var workspace = SourceGit.Remote.RemoteWorkspace.ForPath(WorkingDirectory);
                    var handle = await workspace.OpenTransientAsync(WorkingDirectory, CancellationToken).ConfigureAwait(false);
                    try
                    {
                        var snapshot = await workspace.Client.GetStatusAsync(handle.Id, _noOptionalLocks, CancellationToken).ConfigureAwait(false);
                        Generation = snapshot.Generation;
                        var remote = new List<Models.Change>();
                        foreach (var info in snapshot.Changes)
                        {
                            if (!_includeUntracked && info.WorkingTreeStatus == "?") continue;
                            remote.Add(MapChange(info));
                        }
                        return remote;
                    }
                    finally { await workspace.ReleaseTransientAsync(handle.Id).ConfigureAwait(false); }
                }
                catch { return new List<Models.Change>(); }
            }
            var outs = new List<Models.Change>();

            try
            {
                using var proc = new Process();
                var captured = new CapturedProcess() { Process = proc };
                var capturedLock = new object();

                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                if (CancellationToken.CanBeCanceled)
                {
                    CancellationToken.Register(() =>
                    {
                        lock (capturedLock)
                        {
                            if (captured is { Process: { HasExited: false } proc })
                                Native.OS.TerminateProcess(proc);
                        }
                    });
                }

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var match = REG_FORMAT().Match(line);
                    if (!match.Success)
                        continue;

                    var change = new Models.Change() { Path = match.Groups[2].Value };
                    var status = match.Groups[1].Value;

                    switch (status)
                    {
                        case " M":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Modified);
                            break;
                        case " T":
                            change.Set(Models.ChangeState.None, Models.ChangeState.TypeChanged);
                            break;
                        case " A":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Added);
                            break;
                        case " D":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Deleted);
                            break;
                        case " R":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Renamed);
                            break;
                        case " C":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Copied);
                            break;
                        case "M":
                            change.Set(Models.ChangeState.Modified);
                            break;
                        case "MM":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.Modified);
                            break;
                        case "MT":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.TypeChanged);
                            break;
                        case "MD":
                            change.Set(Models.ChangeState.Modified, Models.ChangeState.Deleted);
                            break;
                        case "T":
                            change.Set(Models.ChangeState.TypeChanged);
                            break;
                        case "TM":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.Modified);
                            break;
                        case "TT":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.TypeChanged);
                            break;
                        case "TD":
                            change.Set(Models.ChangeState.TypeChanged, Models.ChangeState.Deleted);
                            break;
                        case "A":
                            change.Set(Models.ChangeState.Added);
                            break;
                        case "AM":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.Modified);
                            break;
                        case "AT":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.TypeChanged);
                            break;
                        case "AD":
                            change.Set(Models.ChangeState.Added, Models.ChangeState.Deleted);
                            break;
                        case "D":
                            change.Set(Models.ChangeState.Deleted);
                            break;
                        case "R":
                            change.Set(Models.ChangeState.Renamed);
                            break;
                        case "RM":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.Modified);
                            break;
                        case "RT":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.TypeChanged);
                            break;
                        case "RD":
                            change.Set(Models.ChangeState.Renamed, Models.ChangeState.Deleted);
                            break;
                        case "C":
                            change.Set(Models.ChangeState.Copied);
                            break;
                        case "CM":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.Modified);
                            break;
                        case "CT":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.TypeChanged);
                            break;
                        case "CD":
                            change.Set(Models.ChangeState.Copied, Models.ChangeState.Deleted);
                            break;
                        case "DD":
                            change.ConflictReason = Models.ConflictReason.BothDeleted;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "AU":
                            change.ConflictReason = Models.ConflictReason.AddedByUs;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "UD":
                            change.ConflictReason = Models.ConflictReason.DeletedByThem;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "UA":
                            change.ConflictReason = Models.ConflictReason.AddedByThem;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "DU":
                            change.ConflictReason = Models.ConflictReason.DeletedByUs;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "AA":
                            change.ConflictReason = Models.ConflictReason.BothAdded;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "UU":
                            change.ConflictReason = Models.ConflictReason.BothModified;
                            change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
                            break;
                        case "??":
                            change.Set(Models.ChangeState.None, Models.ChangeState.Untracked);
                            break;
                    }

                    if (change.Index != Models.ChangeState.None || change.WorkTree != Models.ChangeState.None)
                        outs.Add(change);
                }

                lock (capturedLock)
                {
                    captured.Process = null;
                }
            }
            catch
            {
                // Ignore exceptions.
            }

            return outs;
        }
        private static Models.Change MapChange(SourceGit.Contracts.ChangeInfo info)
        {
            var change = new Models.Change() { Path = info.Path, OriginalPath = info.OriginalPath ?? string.Empty };
            if (info.Conflict)
            {
                var conflictPair = info.IndexStatus + info.WorkingTreeStatus;
                change.ConflictReason = conflictPair switch
                {
                    "DD" => Models.ConflictReason.BothDeleted,
                    "AU" => Models.ConflictReason.AddedByUs,
                    "UD" => Models.ConflictReason.DeletedByThem,
                    "UA" => Models.ConflictReason.AddedByThem,
                    "DU" => Models.ConflictReason.DeletedByUs,
                    "AA" => Models.ConflictReason.BothAdded,
                    _ => Models.ConflictReason.BothModified,
                };
                change.Set(Models.ChangeState.None, Models.ChangeState.Conflicted);
            }
            else
            {
                change.Set(MapState(info.IndexStatus), MapState(info.WorkingTreeStatus));
            }
            return change;
        }
        private static Models.ChangeState MapState(string status) => status.Length > 0 ? status[0] switch
        {
            'M' => Models.ChangeState.Modified,
            'T' => Models.ChangeState.TypeChanged,
            'A' => Models.ChangeState.Added,
            'D' => Models.ChangeState.Deleted,
            'R' => Models.ChangeState.Renamed,
            'C' => Models.ChangeState.Copied,
            'U' => Models.ChangeState.Conflicted,
            '?' => Models.ChangeState.Untracked,
            _ => Models.ChangeState.None,
        } : Models.ChangeState.None;

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }
    }
}
