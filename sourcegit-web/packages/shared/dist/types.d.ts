/**
 * Shared TypeScript types for the SourceGit Web GUI.
 *
 * These types are consumed by both the client (React) and server (Express)
 * packages.  Keep this file self-contained — no runtime dependencies.
 */
/** Human-readable git status character for a file change. */
export type FileStatus = 'A' | 'M' | 'D' | 'R' | 'C' | 'U' | '??' | 'AM' | 'AU' | 'AD' | 'AA' | 'DD' | 'UD' | 'UU';
/** Symlink / git-file operation indicator. */
export type FileOperation = ' ' | 'L' | 'S' | 'x' | '-';
/** Single file change entry from git status / git diff. */
export interface FileChange {
    path: string;
    status: FileStatus;
    operation: FileOperation;
    newFile: boolean;
    score?: number;
}
/** Current working-tree status snapshot. */
export interface GitStatus {
    staged: FileChange[];
    unstaged: FileChange[];
    untracked: FileChange[];
    currentBranch?: string;
    upstreamBranch?: string;
    aheadBy?: number;
    behindBy?: number;
}
/** Author or committer information. */
export interface CommitAuthor {
    name: string;
    email: string;
    date: string;
}
/** Single commit as returned by log queries. */
export interface Commit {
    sha: string;
    shortSha: string;
    message: string;
    shortMessage: string;
    author: CommitAuthor;
    committer: CommitAuthor;
    parents: string[];
    tags: string[];
    treeSha: string;
    commitCount: number;
    graphLine?: number;
}
export interface Branch {
    name: string;
    shortName: string;
    sha: string;
    upstream?: string;
    isCurrent: boolean;
    isLocal: boolean;
}
export interface Remote {
    name: string;
    url: string;
    pushUrl?: string;
}
export interface Tag {
    name: string;
    sha: string;
    message?: string;
}
export interface Worktree {
    path: string;
    isCurrent: boolean;
    isBare: boolean;
    headCommit?: string;
}
export interface StashEntry {
    index: number;
    sha: string;
    message: string;
    isIndex: boolean;
}
export type DiffLineType = ' ' | '+' | '-' | '@';
export interface DiffLine {
    type: DiffLineType;
    content: string;
    lineBefore?: number;
    lineAfter?: number;
}
export interface Diff {
    oldPath: string;
    newPath: string;
    oldMode?: string;
    newMode?: string;
    isBinary: boolean;
    lines: DiffLine[];
}
export interface BlameLine {
    sha: string;
    shortSha: string;
    author: string;
    date: string;
    newLine: number;
    content: string;
}
/** Spec for a single git command invocation. */
export interface GitCommandSpec {
    args: string[];
    cwd?: string;
    env?: Record<string, string>;
    description?: string;
}
/** Result of a completed command. */
export interface CommandResult {
    exitCode: number;
    stdout: string;
    stderr: string;
    command: string;
    duration: number;
}
/** Callback-based streaming interface for long-running commands. */
export interface CommandStream {
    onStdout?: (chunk: string) => void;
    onStderr?: (chunk: string) => void;
    onProgress?: (progress: number, total: number) => void;
}
/** Client to Server request. */
export interface WsRequestMessage {
    id: string;
    method: string;
    params?: Record<string, unknown>;
}
/** Server to Client successful result. */
export interface WsResultMessage {
    id: string;
    result: unknown;
}
/** Server to Client error. */
export interface WsErrorMessage {
    id: string;
    error: {
        code: string;
        message: string;
    };
}
/** Server to Client progress update (streamed). */
export interface WsProgressMessage {
    id: string;
    progress: {
        current: number;
        total: number;
        message?: string;
    };
}
/** Union of all WebSocket message shapes. */
export type WsMessage = WsRequestMessage | WsResultMessage | WsErrorMessage | WsProgressMessage;
/** A discoverable local repository entry. */
export interface RepoEntry {
    name: string;
    path: string;
    isBare?: boolean;
}
export interface GetReposResponse {
    repos: RepoEntry[];
}
export interface GetRepoStatusResponse extends GitStatus {
    repo: RepoEntry;
}
export interface GetCommitsResponse {
    commits: Commit[];
    total: number;
}
export interface GetBranchesResponse {
    branches: Branch[];
}
export interface GetRemotesResponse {
    remotes: Remote[];
}
export interface GetTagsResponse {
    tags: Tag[];
}
export interface GetStashesResponse {
    stashes: StashEntry[];
}
export interface GetDiffResponse {
    diff: Diff;
}
export interface GetBlameResponse {
    lines: BlameLine[];
}
export interface GetWorktreesResponse {
    worktrees: Worktree[];
}
export interface LogOptions {
    maxCount?: number;
    afterDate?: string;
    beforeDate?: string;
    author?: string;
    message?: string;
    filePaths?: string[];
    topoOrder?: boolean;
    noMerges?: boolean;
}
export interface PullOptions {
    remote?: string;
    branch?: string;
    rebase?: boolean;
}
export interface PushOptions {
    remote?: string;
    branch?: string;
    force?: boolean;
    tags?: boolean;
}
export interface GitConfigEntry {
    key: string;
    value: string;
    scope: 'system' | 'global' | 'local' | 'worktree';
}
export interface GitError {
    code: string;
    message: string;
    command?: string;
    stderr?: string;
}
export interface RepositoryInfo {
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
//# sourceMappingURL=types.d.ts.map