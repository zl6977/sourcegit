/**
 * Operation type definitions for the SourceGit Web GUI.
 *
 * Every git operation exposed over the WebSocket has an input interface
 * (what the client sends) and an output interface (what the server
 * returns).  The `OPERATION_MAP` provides the canonical name→descriptor
 * mapping used by the client and server to agree on semantics.
 */

import type {
  Commit,
  Branch,
  Remote,
  Tag,
  StashEntry,
  FileChange,
  GitStatus,
  Diff,
  BlameLine,
  Worktree,
  GitConfigEntry,
  LogOptions,
  PullOptions,
  PushOptions,
} from './types.js';

/* ─────────────────────────────────────────────
 *  Operation Descriptor
 * ───────────────────────────────────────────── */

/** Human-readable label for the operation. */
export interface OperationDescriptor {
  name: string;
  description: string;
  category: 'status' | 'working_copy' | 'commit' | 'branch' | 'remote' | 'tag' | 'stash' | 'diff' | 'config' | 'worktree';
}

/* ─────────────────────────────────────────────
 *  Input / Output Pairs
 * ───────────────────────────────────────────── */

/* ── Status ────────────────────────────────── */

export interface GetStatusInput { repo: string }
export interface GetStatusOutput extends GitStatus { pathCount: number }

/* ── Working Copy ──────────────────────────── */

export interface StagePathsInput { repo: string; paths: string[] }
export interface StagePathsOutput { staged: number; status: GitStatus }

export interface UnstagePathsInput { repo: string; paths: string[] }
export interface UnstagePathsOutput { unstaged: number; status: GitStatus }

export interface AddPathsInput { repo: string; paths: string[] }
export interface AddPathsOutput { added: number; status: GitStatus }

export interface DiscardChangesInput { repo: string; paths: string[] }
export interface DiscardChangesOutput { discarded: number; status: GitStatus }

export interface RestorePathsInput { repo: string; paths: string[]; revision?: string; staged?: boolean }
export interface RestorePathsOutput { restored: number; status: GitStatus }

export interface RemovePathsInput { repo: string; paths: string[]; cached?: boolean; force?: boolean }
export interface RemovePathsOutput { removed: number; status: GitStatus }

/* ── Commit ────────────────────────────────── */

export interface CommitInput {
  repo: string;
  message: string;
  signOff?: boolean;
  amend?: boolean;
  noVerify?: boolean;
  all?: boolean;
  author?: string;
}
export interface CommitOutput { commit: Commit }

export interface AmendCommitInput {
  repo: string;
  message?: string;
  noEdit?: boolean;
  resetAuthor?: boolean;
}
export interface AmendCommitOutput { commit: Commit }

export interface RevertCommitInput { repo: string; sha: string; noCommit?: boolean; edit?: boolean }
export interface RevertCommitOutput { commit?: Commit }

export interface ResetRevisionInput {
  repo: string;
  revision: string;
  mode: 'soft' | 'mixed' | 'hard';
}
export interface ResetRevisionOutput { status: GitStatus }

/* ── Commits (query) ───────────────────────── */

export interface QueryCommitInput { repo: string; revision: string }
export interface QueryCommitOutput { commit: Commit }

export interface QueryCommitsInput {
  repo: string;
  options?: LogOptions;
  branch?: string;
}
export interface QueryCommitsOutput { commits: Commit[]; total: number }

/* ── Branch ────────────────────────────────── */

export interface ListBranchesInput {
  repo: string;
  remote?: boolean;
  contains?: string;
}
export interface ListBranchesOutput { branches: Branch[] }

export interface CreateBranchInput {
  repo: string;
  name: string;
  startPoint?: string;
  track?: boolean;
}
export interface CreateBranchOutput { branch: Branch }

export interface CheckoutBranchInput {
  repo: string;
  name: string;
  create?: boolean;
  startPoint?: string;
}
export interface CheckoutBranchOutput { branch: Branch; status: GitStatus }

export interface DeleteBranchInput { repo: string; name: string; force?: boolean }
export interface DeleteBranchOutput { deleted: boolean }

export interface MergeBranchInput {
  repo: string;
  branch: string;
  strategy?: string;
  noFastForward?: boolean;
  squash?: boolean;
}
export interface MergeBranchOutput { commit?: Commit; status: 'success' | 'conflict' }

/* ── Remote ────────────────────────────────── */

export interface ListRemotesInput { repo: string }
export interface ListRemotesOutput { remotes: Remote[] }

export interface AddRemoteInput { repo: string; name: string; url: string }
export interface AddRemoteOutput { remote: Remote }

export interface RemoveRemoteInput { repo: string; name: string }
export interface RemoveRemoteOutput { deleted: boolean }

export interface FetchRemoteInput {
  repo: string;
  remote?: string;
  refSpec?: string;
  prune?: boolean;
  depth?: number;
  tags?: boolean;
}
export interface FetchRemoteOutput { fetchedRefs: string[] }

export interface PullRemoteInput {
  repo: string;
  options?: PullOptions;
}
export interface PullRemoteOutput { commits: number; status: 'up_to_date' | 'success' | 'conflict' }

export interface PushRemoteInput {
  repo: string;
  options?: PushOptions;
}
export interface PushRemoteOutput { pushedRefs: string[]; status: 'up_to_date' | 'success' | 'rejected' }

/* ── Tag ───────────────────────────────────── */

export interface ListTagsInput { repo: string }
export interface ListTagsOutput { tags: Tag[] }

export interface CreateTagInput {
  repo: string;
  name: string;
  revision?: string;
  message?: string;
  annotated?: boolean;
  signingKey?: string;
}
export interface CreateTagOutput { tag: Tag }

export interface DeleteTagInput { repo: string; name: string }
export interface DeleteTagOutput { deleted: boolean }

export interface PushTagsInput { repo: string; remote?: string; force?: boolean }
export interface PushTagsOutput { pushed: string[] }

/* ── Stash ─────────────────────────────────── */

export interface ListStashesInput { repo: string }
export interface ListStashesOutput { stashes: StashEntry[] }

export interface StashPushInput {
  repo: string;
  message?: string;
  includeUntracked?: boolean;
  keepIndex?: boolean;
}
export interface StashPushOutput { stash: StashEntry; status: GitStatus }

export interface StashApplyInput { repo: string; index?: number; keepIndex?: boolean; includeUntracked?: boolean }
export interface StashApplyOutput { status: GitStatus }

export interface StashPopInput { repo: string; index?: number; includeUntracked?: boolean }
export interface StashPopOutput { status: GitStatus }

export interface StashDropInput { repo: string; index?: number }
export interface StashDropOutput { dropped: boolean }

export interface StashClearInput { repo: string }
export interface StashClearOutput { cleared: boolean }

/* ── Diff ──────────────────────────────────── */

export interface ReadDiffInput {
  repo: string;
  path?: string;
  revision?: string;
  revision2?: string;
}
export interface ReadDiffOutput { diff: Diff }

export interface CompareRevisionsInput {
  repo: string;
  revision1: string;
  revision2: string;
  paths?: string[];
}
export interface CompareRevisionsOutput { diff: Diff; files: FileChange[] }

export interface GetBlameInput {
  repo: string;
  path: string;
  revision?: string;
}
export interface GetBlameOutput { lines: BlameLine[] }

/* ── File Content ──────────────────────────── */

export interface ReadFileInput {
  repo: string;
  path: string;
  revision?: string;
}
export interface ReadFileOutput { content: string; path: string; isBinary: boolean }

/* ── Config ────────────────────────────────── */

export interface GetConfigInput { repo: string; key?: string; scope?: 'system' | 'global' | 'local' }
export interface GetConfigOutput { entries: GitConfigEntry[] }

export interface SetConfigInput {
  repo: string;
  key: string;
  value: string;
  scope?: 'system' | 'global' | 'local';
}
export interface SetConfigOutput { entry: GitConfigEntry }

/* ── Worktree ──────────────────────────────── */

export interface ListWorktreesInput { repo: string }
export interface ListWorktreesOutput { worktrees: Worktree[] }

export interface AddWorktreeInput {
  repo: string;
  path: string;
  branch?: string;
  detach?: boolean;
}
export interface AddWorktreeOutput { worktree: Worktree }

export interface RemoveWorktreeInput { repo: string; path: string; force?: boolean }
export interface RemoveWorktreeOutput { deleted: boolean }

/* ── Repo Discovery ────────────────────────── */

export interface GetReposInput { searchPath?: string }
export interface GetReposOutput { repos: string[] }

export interface GetRepoInfoInput { repo: string }
export interface GetRepoInfoOutput {
  path: string;
  name: string;
  isBare: boolean;
  isWorktree: boolean;
  headCommit?: string;
  remoteUrl?: string;
  remoteName?: string;
  currentBranch?: string;
  upstreamBranch?: string;
  gitDir: string;
}

/* ── Submodule ─────────────────────────────── */

export interface ListSubmodulesInput { repo: string }
export interface ListSubmodulesOutput {
  submodules: { name: string; path: string; url: string; sha?: string }[];
}

export interface SubmoduleInitInput { repo: string; all?: boolean }
export interface SubmoduleInitOutput { initialized: number }

export interface SubmoduleUpdateInput { repo: string; all?: boolean; recursive?: boolean }
export interface SubmoduleUpdateOutput { updated: number }

/* ─────────────────────────────────────────────
 *  Operation Map
 * ───────────────────────────────────────────── */

export const OPERATION_MAP: { [K in keyof OperationInputs]: OperationDescriptor } = {
  get_status:        { name: 'get_status',         description: 'Get current repository status',         category: 'status' },
  stage_paths:       { name: 'stage_paths',        description: 'Stage specified file paths',            category: 'working_copy' },
  unstage_paths:     { name: 'unstage_paths',      description: 'Unstage specified file paths',          category: 'working_copy' },
  add_paths:         { name: 'add_paths',          description: 'Add untracked files to the index',      category: 'working_copy' },
  discard_changes:   { name: 'discard_changes',    description: 'Discard working-tree changes',          category: 'working_copy' },
  restore_paths:     { name: 'restore_paths',      description: 'Restore files to a revision',           category: 'working_copy' },
  remove_paths:      { name: 'remove_paths',       description: 'Remove files (rm/rm --cached)',         category: 'working_copy' },

  commit:            { name: 'commit',             description: 'Create a new commit',                   category: 'commit' },
  amend_commit:      { name: 'amend_commit',       description: 'Amend the latest commit',               category: 'commit' },
  revert_commit:     { name: 'revert_commit',      description: 'Revert a commit',                       category: 'commit' },
  reset_revision:    { name: 'reset_revision',     description: 'Reset HEAD to a revision',              category: 'commit' },

  query_commit:      { name: 'query_commit',       description: 'Query a single commit by revision',     category: 'commit' },
  query_commits:     { name: 'query_commits',      description: 'Query commit history with options',     category: 'commit' },

  list_branches:     { name: 'list_branches',      description: 'List branches (local and/or remote)',   category: 'branch' },
  create_branch:     { name: 'create_branch',      description: 'Create a new branch',                   category: 'branch' },
  checkout_branch:   { name: 'checkout_branch',    description: 'Checkout a branch',                     category: 'branch' },
  delete_branch:     { name: 'delete_branch',      description: 'Delete a branch',                       category: 'branch' },
  merge_branch:      { name: 'merge_branch',       description: 'Merge a branch into current HEAD',      category: 'branch' },

  list_remotes:      { name: 'list_remotes',       description: 'List configured remotes',               category: 'remote' },
  add_remote:        { name: 'add_remote',         description: 'Add a remote',                          category: 'remote' },
  remove_remote:     { name: 'remove_remote',      description: 'Remove a remote',                       category: 'remote' },
  fetch_remote:      { name: 'fetch_remote',       description: 'Fetch from a remote',                   category: 'remote' },
  pull_remote:       { name: 'pull_remote',        description: 'Pull from a remote',                    category: 'remote' },
  push_remote:       { name: 'push_remote',        description: 'Push to a remote',                      category: 'remote' },

  list_tags:         { name: 'list_tags',          description: 'List tags',                             category: 'tag' },
  create_tag:        { name: 'create_tag',         description: 'Create a tag',                          category: 'tag' },
  delete_tag:        { name: 'delete_tag',         description: 'Delete a tag',                          category: 'tag' },
  push_tags:         { name: 'push_tags',          description: 'Push tags to a remote',                 category: 'tag' },

  list_stashes:      { name: 'list_stashes',       description: 'List stash entries',                    category: 'stash' },
  stash_push:        { name: 'stash_push',         description: 'Push changes to the stash',             category: 'stash' },
  stash_apply:       { name: 'stash_apply',        description: 'Apply a stash entry',                   category: 'stash' },
  stash_pop:         { name: 'stash_pop',          description: 'Pop and apply a stash entry',           category: 'stash' },
  stash_drop:        { name: 'stash_drop',         description: 'Drop a stash entry',                    category: 'stash' },
  stash_clear:       { name: 'stash_clear',        description: 'Clear all stashes',                     category: 'stash' },

  read_diff:         { name: 'read_diff',          description: 'Read diff for a file or revision',      category: 'diff' },
  compare_revisions: { name: 'compare_revisions',  description: 'Compare two revisions',                 category: 'diff' },
  get_blame:         { name: 'get_blame',          description: 'Get blame info for a file',             category: 'diff' },

  read_file:         { name: 'read_file',          description: 'Read file content at a revision',       category: 'diff' },

  get_config:        { name: 'get_config',         description: 'Get git config entries',                category: 'config' },
  set_config:        { name: 'set_config',         description: 'Set a git config value',                category: 'config' },

  list_worktrees:    { name: 'list_worktrees',     description: 'List worktrees',                        category: 'worktree' },
  add_worktree:      { name: 'add_worktree',       description: 'Add a worktree',                        category: 'worktree' },
  remove_worktree:   { name: 'remove_worktree',    description: 'Remove a worktree',                     category: 'worktree' },

  get_repos:         { name: 'get_repos',          description: 'Discover git repositories',             category: 'status' },
  get_repo_info:     { name: 'get_repo_info',      description: 'Get repository metadata',               category: 'status' },

  list_submodules:   { name: 'list_submodules',    description: 'List submodules',                       category: 'status' },
  submodule_init:    { name: 'submodule_init',     description: 'Initialize submodules',                 category: 'status' },
  submodule_update:  { name: 'submodule_update',   description: 'Update submodules',                     category: 'status' },
};

/** Ordered array of all operation names. */
export const ALL_OPERATION_NAMES = Object.keys(OPERATION_MAP) as string[];

/** Type-safe key for `OPERATION_MAP`. */
export type OperationName = keyof typeof OPERATION_MAP;

/* ─────────────────────────────────────────────
 *  Union Helpers
 * ───────────────────────────────────────────── */

/** All possible input shapes indexed by operation name. */
export interface OperationInputs {
  get_status:        GetStatusInput;
  stage_paths:       StagePathsInput;
  unstage_paths:     UnstagePathsInput;
  add_paths:         AddPathsInput;
  discard_changes:   DiscardChangesInput;
  restore_paths:     RestorePathsInput;
  remove_paths:      RemovePathsInput;
  commit:            CommitInput;
  amend_commit:      AmendCommitInput;
  revert_commit:     RevertCommitInput;
  reset_revision:    ResetRevisionInput;
  query_commit:      QueryCommitInput;
  query_commits:     QueryCommitsInput;
  list_branches:     ListBranchesInput;
  create_branch:     CreateBranchInput;
  checkout_branch:   CheckoutBranchInput;
  delete_branch:     DeleteBranchInput;
  merge_branch:      MergeBranchInput;
  list_remotes:      ListRemotesInput;
  add_remote:        AddRemoteInput;
  remove_remote:     RemoveRemoteInput;
  fetch_remote:      FetchRemoteInput;
  pull_remote:       PullRemoteInput;
  push_remote:       PushRemoteInput;
  list_tags:         ListTagsInput;
  create_tag:        CreateTagInput;
  delete_tag:        DeleteTagInput;
  push_tags:         PushTagsInput;
  list_stashes:      ListStashesInput;
  stash_push:        StashPushInput;
  stash_apply:       StashApplyInput;
  stash_pop:         StashPopInput;
  stash_drop:        StashDropInput;
  stash_clear:       StashClearInput;
  read_diff:         ReadDiffInput;
  compare_revisions: CompareRevisionsInput;
  get_blame:         GetBlameInput;
  read_file:         ReadFileInput;
  get_config:        GetConfigInput;
  set_config:        SetConfigInput;
  list_worktrees:    ListWorktreesInput;
  add_worktree:      AddWorktreeInput;
  remove_worktree:   RemoveWorktreeInput;
  get_repos:         GetReposInput;
  get_repo_info:     GetRepoInfoInput;
  list_submodules:   ListSubmodulesInput;
  submodule_init:    SubmoduleInitInput;
  submodule_update:  SubmoduleUpdateInput;
}

/** All possible output shapes indexed by operation name. */
export interface OperationOutputs {
  get_status:        GetStatusOutput;
  stage_paths:       StagePathsOutput;
  unstage_paths:     UnstagePathsOutput;
  add_paths:         AddPathsOutput;
  discard_changes:   DiscardChangesOutput;
  restore_paths:     RestorePathsOutput;
  remove_paths:      RemovePathsOutput;
  commit:            CommitOutput;
  amend_commit:      AmendCommitOutput;
  revert_commit:     RevertCommitOutput;
  reset_revision:    ResetRevisionOutput;
  query_commit:      QueryCommitOutput;
  query_commits:     QueryCommitsOutput;
  list_branches:     ListBranchesOutput;
  create_branch:     CreateBranchOutput;
  checkout_branch:   CheckoutBranchOutput;
  delete_branch:     DeleteBranchOutput;
  merge_branch:      MergeBranchOutput;
  list_remotes:      ListRemotesOutput;
  add_remote:        AddRemoteOutput;
  remove_remote:     RemoveRemoteOutput;
  fetch_remote:      FetchRemoteOutput;
  pull_remote:       PullRemoteOutput;
  push_remote:       PushRemoteOutput;
  list_tags:         ListTagsOutput;
  create_tag:        CreateTagOutput;
  delete_tag:        DeleteTagOutput;
  push_tags:         PushTagsOutput;
  list_stashes:      ListStashesOutput;
  stash_push:        StashPushOutput;
  stash_apply:       StashApplyOutput;
  stash_pop:         StashPopOutput;
  stash_drop:        StashDropOutput;
  stash_clear:       StashClearOutput;
  read_diff:         ReadDiffOutput;
  compare_revisions: CompareRevisionsOutput;
  get_blame:         GetBlameOutput;
  read_file:         ReadFileOutput;
  get_config:        GetConfigOutput;
  set_config:        SetConfigOutput;
  list_worktrees:    ListWorktreesOutput;
  add_worktree:      AddWorktreeOutput;
  remove_worktree:   RemoveWorktreeOutput;
  get_repos:         GetReposOutput;
  get_repo_info:     GetRepoInfoOutput;
  list_submodules:   ListSubmodulesOutput;
  submodule_init:    SubmoduleInitOutput;
  submodule_update:  SubmoduleUpdateOutput;
}

/** Generic input with the repo field. */
export type GenericOpInput<N extends OperationName> = OperationInputs[N];

/** Generic output for a given operation name. */
export type GenericOpOutput<N extends OperationName> = OperationOutputs[N];

/** Discriminated union of all input types. */
export type AnyOpInput = { op: OperationName } & { data: OperationInputs[OperationName] };

/** Discriminated union of all output types. */
export type AnyOpOutput = { op: OperationName } & { data: OperationOutputs[OperationName] };