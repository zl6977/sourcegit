/**
 * Operation type definitions for the SourceGit Web GUI.
 *
 * Every git operation exposed over the WebSocket has an input interface
 * (what the client sends) and an output interface (what the server
 * returns).  The `OPERATION_MAP` provides the canonical name→descriptor
 * mapping used by the client and server to agree on semantics.
 */
import type { Commit, Branch, Remote, Tag, StashEntry, FileChange, GitStatus, Diff, BlameLine, Worktree, GitConfigEntry, LogOptions, PullOptions, PushOptions } from './types.js';
/** Human-readable label for the operation. */
export interface OperationDescriptor {
    name: string;
    description: string;
    category: 'status' | 'working_copy' | 'commit' | 'branch' | 'remote' | 'tag' | 'stash' | 'diff' | 'config' | 'worktree';
}
export interface GetStatusInput {
    repo: string;
}
export interface GetStatusOutput extends GitStatus {
    pathCount: number;
}
export interface StagePathsInput {
    repo: string;
    paths: string[];
}
export interface StagePathsOutput {
    staged: number;
    status: GitStatus;
}
export interface UnstagePathsInput {
    repo: string;
    paths: string[];
}
export interface UnstagePathsOutput {
    unstaged: number;
    status: GitStatus;
}
export interface AddPathsInput {
    repo: string;
    paths: string[];
}
export interface AddPathsOutput {
    added: number;
    status: GitStatus;
}
export interface DiscardChangesInput {
    repo: string;
    paths: string[];
}
export interface DiscardChangesOutput {
    discarded: number;
    status: GitStatus;
}
export interface RestorePathsInput {
    repo: string;
    paths: string[];
    revision?: string;
    staged?: boolean;
}
export interface RestorePathsOutput {
    restored: number;
    status: GitStatus;
}
export interface RemovePathsInput {
    repo: string;
    paths: string[];
    cached?: boolean;
    force?: boolean;
}
export interface RemovePathsOutput {
    removed: number;
    status: GitStatus;
}
export interface CommitInput {
    repo: string;
    message: string;
    signOff?: boolean;
    amend?: boolean;
    noVerify?: boolean;
    all?: boolean;
    author?: string;
}
export interface CommitOutput {
    commit: Commit;
}
export interface AmendCommitInput {
    repo: string;
    message?: string;
    noEdit?: boolean;
    resetAuthor?: boolean;
}
export interface AmendCommitOutput {
    commit: Commit;
}
export interface RevertCommitInput {
    repo: string;
    sha: string;
    noCommit?: boolean;
    edit?: boolean;
}
export interface RevertCommitOutput {
    commit?: Commit;
}
export interface ResetRevisionInput {
    repo: string;
    revision: string;
    mode: 'soft' | 'mixed' | 'hard';
}
export interface ResetRevisionOutput {
    status: GitStatus;
}
export interface QueryCommitInput {
    repo: string;
    revision: string;
}
export interface QueryCommitOutput {
    commit: Commit;
}
export interface QueryCommitsInput {
    repo: string;
    options?: LogOptions;
    branch?: string;
}
export interface QueryCommitsOutput {
    commits: Commit[];
    total: number;
}
export interface ListBranchesInput {
    repo: string;
    remote?: boolean;
    contains?: string;
}
export interface ListBranchesOutput {
    branches: Branch[];
}
export interface CreateBranchInput {
    repo: string;
    name: string;
    startPoint?: string;
    track?: boolean;
}
export interface CreateBranchOutput {
    branch: Branch;
}
export interface CheckoutBranchInput {
    repo: string;
    name: string;
    create?: boolean;
    startPoint?: string;
}
export interface CheckoutBranchOutput {
    branch: Branch;
    status: GitStatus;
}
export interface DeleteBranchInput {
    repo: string;
    name: string;
    force?: boolean;
}
export interface DeleteBranchOutput {
    deleted: boolean;
}
export interface MergeBranchInput {
    repo: string;
    branch: string;
    strategy?: string;
    noFastForward?: boolean;
    squash?: boolean;
}
export interface MergeBranchOutput {
    commit?: Commit;
    status: 'success' | 'conflict';
}
export interface ListRemotesInput {
    repo: string;
}
export interface ListRemotesOutput {
    remotes: Remote[];
}
export interface AddRemoteInput {
    repo: string;
    name: string;
    url: string;
}
export interface AddRemoteOutput {
    remote: Remote;
}
export interface RemoveRemoteInput {
    repo: string;
    name: string;
}
export interface RemoveRemoteOutput {
    deleted: boolean;
}
export interface FetchRemoteInput {
    repo: string;
    remote?: string;
    refSpec?: string;
    prune?: boolean;
    depth?: number;
    tags?: boolean;
}
export interface FetchRemoteOutput {
    fetchedRefs: string[];
}
export interface PullRemoteInput {
    repo: string;
    options?: PullOptions;
}
export interface PullRemoteOutput {
    commits: number;
    status: 'up_to_date' | 'success' | 'conflict';
}
export interface PushRemoteInput {
    repo: string;
    options?: PushOptions;
}
export interface PushRemoteOutput {
    pushedRefs: string[];
    status: 'up_to_date' | 'success' | 'rejected';
}
export interface ListTagsInput {
    repo: string;
}
export interface ListTagsOutput {
    tags: Tag[];
}
export interface CreateTagInput {
    repo: string;
    name: string;
    revision?: string;
    message?: string;
    annotated?: boolean;
    signingKey?: string;
}
export interface CreateTagOutput {
    tag: Tag;
}
export interface DeleteTagInput {
    repo: string;
    name: string;
}
export interface DeleteTagOutput {
    deleted: boolean;
}
export interface PushTagsInput {
    repo: string;
    remote?: string;
    force?: boolean;
}
export interface PushTagsOutput {
    pushed: string[];
}
export interface ListStashesInput {
    repo: string;
}
export interface ListStashesOutput {
    stashes: StashEntry[];
}
export interface StashPushInput {
    repo: string;
    message?: string;
    includeUntracked?: boolean;
    keepIndex?: boolean;
}
export interface StashPushOutput {
    stash: StashEntry;
    status: GitStatus;
}
export interface StashApplyInput {
    repo: string;
    index?: number;
    keepIndex?: boolean;
    includeUntracked?: boolean;
}
export interface StashApplyOutput {
    status: GitStatus;
}
export interface StashPopInput {
    repo: string;
    index?: number;
    includeUntracked?: boolean;
}
export interface StashPopOutput {
    status: GitStatus;
}
export interface StashDropInput {
    repo: string;
    index?: number;
}
export interface StashDropOutput {
    dropped: boolean;
}
export interface StashClearInput {
    repo: string;
}
export interface StashClearOutput {
    cleared: boolean;
}
export interface ReadDiffInput {
    repo: string;
    path?: string;
    revision?: string;
    revision2?: string;
}
export interface ReadDiffOutput {
    diff: Diff;
}
export interface CompareRevisionsInput {
    repo: string;
    revision1: string;
    revision2: string;
    paths?: string[];
}
export interface CompareRevisionsOutput {
    diff: Diff;
    files: FileChange[];
}
export interface GetBlameInput {
    repo: string;
    path: string;
    revision?: string;
}
export interface GetBlameOutput {
    lines: BlameLine[];
}
export interface ReadFileInput {
    repo: string;
    path: string;
    revision?: string;
}
export interface ReadFileOutput {
    content: string;
    path: string;
    isBinary: boolean;
}
export interface GetConfigInput {
    repo: string;
    key?: string;
    scope?: 'system' | 'global' | 'local';
}
export interface GetConfigOutput {
    entries: GitConfigEntry[];
}
export interface SetConfigInput {
    repo: string;
    key: string;
    value: string;
    scope?: 'system' | 'global' | 'local';
}
export interface SetConfigOutput {
    entry: GitConfigEntry;
}
export interface ListWorktreesInput {
    repo: string;
}
export interface ListWorktreesOutput {
    worktrees: Worktree[];
}
export interface AddWorktreeInput {
    repo: string;
    path: string;
    branch?: string;
    detach?: boolean;
}
export interface AddWorktreeOutput {
    worktree: Worktree;
}
export interface RemoveWorktreeInput {
    repo: string;
    path: string;
    force?: boolean;
}
export interface RemoveWorktreeOutput {
    deleted: boolean;
}
export interface GetReposInput {
    searchPath?: string;
}
export interface GetReposOutput {
    repos: string[];
}
export interface GetRepoInfoInput {
    repo: string;
}
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
export interface ListSubmodulesInput {
    repo: string;
}
export interface ListSubmodulesOutput {
    submodules: {
        name: string;
        path: string;
        url: string;
        sha?: string;
    }[];
}
export interface SubmoduleInitInput {
    repo: string;
    all?: boolean;
}
export interface SubmoduleInitOutput {
    initialized: number;
}
export interface SubmoduleUpdateInput {
    repo: string;
    all?: boolean;
    recursive?: boolean;
}
export interface SubmoduleUpdateOutput {
    updated: number;
}
export declare const OPERATION_MAP: {
    [K in keyof OperationInputs]: OperationDescriptor;
};
/** Ordered array of all operation names. */
export declare const ALL_OPERATION_NAMES: string[];
/** Type-safe key for `OPERATION_MAP`. */
export type OperationName = keyof typeof OPERATION_MAP;
/** All possible input shapes indexed by operation name. */
export interface OperationInputs {
    get_status: GetStatusInput;
    stage_paths: StagePathsInput;
    unstage_paths: UnstagePathsInput;
    add_paths: AddPathsInput;
    discard_changes: DiscardChangesInput;
    restore_paths: RestorePathsInput;
    remove_paths: RemovePathsInput;
    commit: CommitInput;
    amend_commit: AmendCommitInput;
    revert_commit: RevertCommitInput;
    reset_revision: ResetRevisionInput;
    query_commit: QueryCommitInput;
    query_commits: QueryCommitsInput;
    list_branches: ListBranchesInput;
    create_branch: CreateBranchInput;
    checkout_branch: CheckoutBranchInput;
    delete_branch: DeleteBranchInput;
    merge_branch: MergeBranchInput;
    list_remotes: ListRemotesInput;
    add_remote: AddRemoteInput;
    remove_remote: RemoveRemoteInput;
    fetch_remote: FetchRemoteInput;
    pull_remote: PullRemoteInput;
    push_remote: PushRemoteInput;
    list_tags: ListTagsInput;
    create_tag: CreateTagInput;
    delete_tag: DeleteTagInput;
    push_tags: PushTagsInput;
    list_stashes: ListStashesInput;
    stash_push: StashPushInput;
    stash_apply: StashApplyInput;
    stash_pop: StashPopInput;
    stash_drop: StashDropInput;
    stash_clear: StashClearInput;
    read_diff: ReadDiffInput;
    compare_revisions: CompareRevisionsInput;
    get_blame: GetBlameInput;
    read_file: ReadFileInput;
    get_config: GetConfigInput;
    set_config: SetConfigInput;
    list_worktrees: ListWorktreesInput;
    add_worktree: AddWorktreeInput;
    remove_worktree: RemoveWorktreeInput;
    get_repos: GetReposInput;
    get_repo_info: GetRepoInfoInput;
    list_submodules: ListSubmodulesInput;
    submodule_init: SubmoduleInitInput;
    submodule_update: SubmoduleUpdateInput;
}
/** All possible output shapes indexed by operation name. */
export interface OperationOutputs {
    get_status: GetStatusOutput;
    stage_paths: StagePathsOutput;
    unstage_paths: UnstagePathsOutput;
    add_paths: AddPathsOutput;
    discard_changes: DiscardChangesOutput;
    restore_paths: RestorePathsOutput;
    remove_paths: RemovePathsOutput;
    commit: CommitOutput;
    amend_commit: AmendCommitOutput;
    revert_commit: RevertCommitOutput;
    reset_revision: ResetRevisionOutput;
    query_commit: QueryCommitOutput;
    query_commits: QueryCommitsOutput;
    list_branches: ListBranchesOutput;
    create_branch: CreateBranchOutput;
    checkout_branch: CheckoutBranchOutput;
    delete_branch: DeleteBranchOutput;
    merge_branch: MergeBranchOutput;
    list_remotes: ListRemotesOutput;
    add_remote: AddRemoteOutput;
    remove_remote: RemoveRemoteOutput;
    fetch_remote: FetchRemoteOutput;
    pull_remote: PullRemoteOutput;
    push_remote: PushRemoteOutput;
    list_tags: ListTagsOutput;
    create_tag: CreateTagOutput;
    delete_tag: DeleteTagOutput;
    push_tags: PushTagsOutput;
    list_stashes: ListStashesOutput;
    stash_push: StashPushOutput;
    stash_apply: StashApplyOutput;
    stash_pop: StashPopOutput;
    stash_drop: StashDropOutput;
    stash_clear: StashClearOutput;
    read_diff: ReadDiffOutput;
    compare_revisions: CompareRevisionsOutput;
    get_blame: GetBlameOutput;
    read_file: ReadFileOutput;
    get_config: GetConfigOutput;
    set_config: SetConfigOutput;
    list_worktrees: ListWorktreesOutput;
    add_worktree: AddWorktreeOutput;
    remove_worktree: RemoveWorktreeOutput;
    get_repos: GetReposOutput;
    get_repo_info: GetRepoInfoOutput;
    list_submodules: ListSubmodulesOutput;
    submodule_init: SubmoduleInitOutput;
    submodule_update: SubmoduleUpdateOutput;
}
/** Generic input with the repo field. */
export type GenericOpInput<N extends OperationName> = OperationInputs[N];
/** Generic output for a given operation name. */
export type GenericOpOutput<N extends OperationName> = OperationOutputs[N];
/** Discriminated union of all input types. */
export type AnyOpInput = {
    op: OperationName;
} & {
    data: OperationInputs[OperationName];
};
/** Discriminated union of all output types. */
export type AnyOpOutput = {
    op: OperationName;
} & {
    data: OperationOutputs[OperationName];
};
//# sourceMappingURL=operations.d.ts.map