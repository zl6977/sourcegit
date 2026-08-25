using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using Avalonia.Collections;

using SourceGit.Commands;

namespace SourceGit.Models
{
    public class RepositoryUIStates
    {
        public HistoryShowFlags HistoryShowFlags
        {
            get;
            set;
        } = HistoryShowFlags.None;

        public bool IsAuthorColumnVisibleInHistory
        {
            get;
            set;
        } = true;

        public bool IsSHAColumnVisibleInHistory
        {
            get;
            set;
        } = true;

        public bool IsAuthorTimeColumnVisibleInHistory
        {
            get;
            set;
        } = false;

        public bool IsCommitTimeColumnVisibleInHistory
        {
            get;
            set;
        } = true;

        public double AuthorColumnWidth
        {
            get;
            set;
        } = 120;

        public bool EnableTopoOrderInHistory
        {
            get;
            set;
        } = false;

        public CommitGraphHighlighting GraphHighlighting
        {
            get;
            set;
        } = CommitGraphHighlighting.All;

        public BranchSortMode LocalBranchSortMode
        {
            get;
            set;
        } = BranchSortMode.Name;

        public BranchSortMode RemoteBranchSortMode
        {
            get;
            set;
        } = BranchSortMode.Name;

        public bool ShowTagsAsTree
        {
            get;
            set;
        } = false;

        public TagSortMode TagSortMode
        {
            get;
            set;
        } = TagSortMode.CreatorDate;

        public bool ShowSubmodulesAsTree
        {
            get;
            set;
        } = false;

        public bool IncludeUntrackedInLocalChanges
        {
            get;
            set;
        } = true;

        public bool EnableForceOnFetch
        {
            get;
            set;
        } = false;

        public bool FetchAllRemotes
        {
            get;
            set;
        } = false;

        public bool FetchWithoutTags
        {
            get;
            set;
        } = false;

        public bool PreferRebaseInsteadOfMerge
        {
            get;
            set;
        } = true;

        public bool PushAllTags
        {
            get;
            set;
        } = false;

        public bool CreateAnnotatedTag
        {
            get;
            set;
        } = true;

        public bool PushToRemoteWhenCreateTag
        {
            get;
            set;
        } = true;

        public bool PushToRemoteWhenDeleteTag
        {
            get;
            set;
        } = false;

        public bool CheckoutBranchOnCreateBranch
        {
            get;
            set;
        } = true;

        public bool EnableSignOffForCommit
        {
            get;
            set;
        } = false;

        public bool NoVerifyOnCommit
        {
            get;
            set;
        } = false;

        public bool IncludeUntrackedWhenStash
        {
            get;
            set;
        } = true;

        public bool OnlyStagedWhenStash
        {
            get;
            set;
        } = false;

        public int ChangesAfterStashing
        {
            get;
            set;
        } = 0;

        public bool IsLocalBranchesExpandedInSideBar
        {
            get;
            set;
        } = true;

        public bool IsRemotesExpandedInSideBar
        {
            get;
            set;
        } = false;

        public bool IsTagsExpandedInSideBar
        {
            get;
            set;
        } = false;

        public bool IsSubmodulesExpandedInSideBar
        {
            get;
            set;
        } = false;

        public bool IsWorktreeExpandedInSideBar
        {
            get;
            set;
        } = false;

        public List<string> ExpandedBranchNodesInSideBar
        {
            get;
            set;
        } = [];

        public string LastCommitMessage
        {
            get;
            set;
        } = string.Empty;

        public AvaloniaList<HistoryFilter> HistoryFilters
        {
            get;
            set;
        } = [];

        public bool IsHistoryFiltersCollapsed
        {
            get;
            set;
        } = false;

        public List<string> RecentCommitMessages
        {
            get;
            set;
        } = [];

        public static RepositoryUIStates Load(string gitDir, Commands.RepositoryController controller = null)
        {
            var fileName = "sourcegit.uistates";
            var fullpath = controller != null ? $"{gitDir}/{fileName}" : Path.Combine(gitDir, fileName);

            var exists = controller != null
                ? controller.GitDirFileExists(fileName)
                : GitService.FileExists(Path.Combine(gitDir, fileName));

            RepositoryUIStates states;
            if (!exists)
            {
                states = new RepositoryUIStates();
            }
            else
            {
                try
                {
                    using var stream = controller != null
                        ? controller.OpenReadGitDirFile(fileName)
                        : (System.IO.Stream)GitService.OpenRead(Path.Combine(gitDir, fileName));
                    states = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositoryUIStates);
                }
                catch
                {
                    states = new RepositoryUIStates();
                }
            }

            states._file = fullpath;
            states._controller = controller;
            return states;
        }

        public void Save()
        {
            try
            {
                var content = JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositoryUIStates);
                if (_controller != null)
                {
                    _controller.WriteGitDirFileAndReplace(Path.GetFileName(_file), content);
                }
                else
                {
                    GitService.WriteFileAndReplace(_file, content);
                }
            }
            catch
            {
                // Ignore save errors
            }
        }

        public FilterMode GetHistoryFilterMode(string pattern = null)
        {
            if (string.IsNullOrEmpty(pattern))
                return HistoryFilters.Count == 0 ? FilterMode.None : HistoryFilters[0].Mode;

            foreach (var filter in HistoryFilters)
            {
                if (filter.Pattern.Equals(pattern, StringComparison.Ordinal))
                    return filter.Mode;
            }

            return FilterMode.None;
        }

        public Dictionary<string, FilterMode> GetHistoryFiltersMap()
        {
            var map = new Dictionary<string, FilterMode>();
            foreach (var filter in HistoryFilters)
                map.Add(filter.Pattern, filter.Mode);
            return map;
        }

        public bool UpdateHistoryFilters(string pattern, FilterType type, FilterMode mode)
        {
            // Clear all filters when there's a filter that has different mode.
            if (mode != FilterMode.None)
            {
                var clear = false;
                foreach (var filter in HistoryFilters)
                {
                    if (filter.Mode != mode)
                    {
                        clear = true;
                        break;
                    }
                }

                if (clear)
                {
                    HistoryFilters.Clear();
                    HistoryFilters.Add(new HistoryFilter(pattern, type, mode));
                    return true;
                }
            }
            else
            {
                for (int i = 0; i < HistoryFilters.Count; i++)
                {
                    var filter = HistoryFilters[i];
                    if (filter.Type == type && filter.Pattern.Equals(pattern, StringComparison.Ordinal))
                    {
                        HistoryFilters.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }

            foreach (var filter in HistoryFilters)
            {
                if (filter.Type != type)
                    continue;

                if (filter.Pattern.Equals(pattern, StringComparison.Ordinal))
                    return false;
            }

            HistoryFilters.Add(new HistoryFilter(pattern, type, mode));
            return true;
        }

        public void RemoveHistoryFilter(string pattern, FilterType type)
        {
            foreach (var filter in HistoryFilters)
            {
                if (filter.Type == type && filter.Pattern.Equals(pattern, StringComparison.Ordinal))
                {
                    HistoryFilters.Remove(filter);
                    break;
                }
            }
        }

        public void RenameBranchFilter(string oldName, string newName)
        {
            foreach (var filter in HistoryFilters)
            {
                if (filter.Type == FilterType.LocalBranch &&
                    filter.Pattern.Equals(oldName, StringComparison.Ordinal))
                {
                    filter.Pattern = $"refs/heads/{newName}";
                    break;
                }
            }
        }

        public void RemoveBranchFiltersByPrefix(string pattern)
        {
            var dirty = new List<HistoryFilter>();
            var prefix = $"{pattern}/";

            foreach (var filter in HistoryFilters)
            {
                if (filter.Type == FilterType.Tag)
                    continue;

                if (filter.Pattern.StartsWith(prefix, StringComparison.Ordinal))
                    dirty.Add(filter);
            }

            foreach (var filter in dirty)
                HistoryFilters.Remove(filter);
        }

        public List<string> BuildHistoryParams(string gitDir)
        {
            var args = new List<string>();

            if (EnableTopoOrderInHistory)
                args.Add("--topo-order");
            else
                args.Add("--date-order");

            if (HistoryShowFlags.HasFlag(HistoryShowFlags.Reflog))
                args.Add("--reflog");

            if (HistoryShowFlags.HasFlag(HistoryShowFlags.FirstParentOnly))
                args.Add("--first-parent");

            if (HistoryShowFlags.HasFlag(HistoryShowFlags.SimplifyByDecoration))
                args.Add("--simplify-by-decoration");

            var mode = GetHistoryFilterMode();
            if (mode == FilterMode.None)
                args.AddRange(["--branches", "--remotes", "--tags", "HEAD"]);
            else if (mode == FilterMode.Included)
                BuildHistoryParamsForIncluded(args);
            else
                BuildHistoryParamsForExcluded(args, gitDir);

            return args;
        }

        private void BuildHistoryParamsForIncluded(List<string> args)
        {
            foreach (var filter in HistoryFilters)
            {
                if (filter.Type == FilterType.LocalBranch)
                    args.Add(filter.Pattern);
                else if (filter.Type == FilterType.LocalBranchFolder)
                    args.Add($"--branches={filter.Pattern.AsSpan(11)}/*");
                else if (filter.Type == FilterType.RemoteBranch)
                    args.Add(filter.Pattern);
                else if (filter.Type == FilterType.RemoteBranchFolder)
                    args.Add($"--remotes={filter.Pattern.AsSpan(13)}/*");
                else if (filter.Type == FilterType.Tag)
                    args.Add($"refs/tags/{filter.Pattern}");
            }
        }

        private void BuildHistoryParamsForExcluded(List<string> args, string gitDir)
        {
            var excludedBranches = new List<string>();
            var excludedRemotes = new List<string>();
            var excludedTags = new List<string>();
            foreach (var filter in HistoryFilters)
            {
                if (filter.Type == FilterType.LocalBranch)
                    excludedBranches.AddRange([$"--exclude={filter.Pattern.AsSpan(11)}", $"--decorate-refs-exclude={filter.Pattern}"]);
                else if (filter.Type == FilterType.LocalBranchFolder)
                    excludedBranches.AddRange([$"--exclude={filter.Pattern.AsSpan(11)}/*", $"--decorate-refs-exclude={filter.Pattern}/*"]);
                else if (filter.Type == FilterType.RemoteBranch)
                    excludedRemotes.AddRange([$"--exclude={filter.Pattern.AsSpan(13)}", $"--decorate-refs-exclude={filter.Pattern}"]);
                else if (filter.Type == FilterType.RemoteBranchFolder)
                    excludedRemotes.AddRange([$"--exclude={filter.Pattern.AsSpan(13)}/*", $"--decorate-refs-exclude={filter.Pattern}/*"]);
                else if (filter.Type == FilterType.Tag)
                    excludedTags.AddRange([$"--exclude={filter.Pattern}", $"--decorate-refs-exclude=refs/tags/{filter.Pattern}"]);
            }

            args.AddRange(excludedBranches);

            args.Add("--branches");

            var isInProgress = _controller != null
                ? (_controller.HasCherryPickHead() || _controller.HasRebaseMerge() || _controller.HasRebaseApply() || _controller.HasRevertHead() || _controller.HasMergeHead())
                : (GitService.FileExists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")) ||
                    GitService.DirectoryExists(Path.Combine(gitDir, "rebase-merge")) ||
                    GitService.DirectoryExists(Path.Combine(gitDir, "rebase-apply")) ||
                    GitService.FileExists(Path.Combine(gitDir, "REVERT_HEAD")) ||
                    GitService.FileExists(Path.Combine(gitDir, "MERGE_HEAD")));
            if (isInProgress)
                args.Add("HEAD");

            args.AddRange(excludedRemotes);

            args.Add("--exclude=origin/HEAD");
            args.Add("--remotes");

            args.AddRange(excludedTags);

            args.Add("--tags");
        }

        public void AddRecentCommitMessage(string message)
        {
            message = message.Trim().ReplaceLineEndings("\n");
            var existIdx = RecentCommitMessages.IndexOf(message);
            if (existIdx == 0)
                return;

            if (existIdx > 0)
            {
                RecentCommitMessages.RemoveAt(existIdx);
                RecentCommitMessages.Insert(0, message);
                return;
            }

            if (RecentCommitMessages.Count > 9)
                RecentCommitMessages.RemoveRange(9, RecentCommitMessages.Count - 9);

            RecentCommitMessages.Insert(0, message);
        }

        private string _file = string.Empty;
        private Commands.RepositoryController _controller = null;
    }
}
