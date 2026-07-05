/**
 * Barrel export for @sourcegit/shared.
 *
 * Re-exports all type definitions and operation descriptors
 * so that client and server packages can import from a single
 * entry point:  `import { ... } from '@sourcegit/shared'`.
 */

/* ── Types ─────────────────────────────────── */
export type {
  // Core model
  FileStatus,
  FileOperation,
  FileChange,
  GitStatus,

  // Commit & history
  CommitAuthor,
  Commit,

  // Branch, remote, tag, worktree
  Branch,
  Remote,
  Tag,
  Worktree,

  // Stash
  StashEntry,

  // Diff & blame
  DiffLineType,
  DiffLine,
  Diff,
  BlameLine,

  // Command execution
  GitCommandSpec,
  CommandResult,
  CommandStream,

  // WebSocket
  WsRequestMessage,
  WsResultMessage,
  WsErrorMessage,
  WsProgressMessage,
  WsMessage,

  // API responses
  GetReposResponse,
  GetRepoStatusResponse,
  GetCommitsResponse,
  GetBranchesResponse,
  GetRemotesResponse,
  GetTagsResponse,
  GetStashesResponse,
  GetDiffResponse,
  GetBlameResponse,
  GetWorktreesResponse,
  RepoEntry,

  // Options
  LogOptions,
  PullOptions,
  PushOptions,

  // Config & error
  GitConfigEntry,
  GitError,

  // Repository info
  RepositoryInfo,
} from './types.js';

/* ── Operations ────────────────────────────── */
export type {
  OperationDescriptor,
  OperationName,

  // Input types
  GetStatusInput,
  StagePathsInput,
  UnstagePathsInput,
  AddPathsInput,
  DiscardChangesInput,
  RestorePathsInput,
  RemovePathsInput,
  CommitInput,
  AmendCommitInput,
  RevertCommitInput,
  ResetRevisionInput,
  QueryCommitInput,
  QueryCommitsInput,
  ListBranchesInput,
  CreateBranchInput,
  CheckoutBranchInput,
  DeleteBranchInput,
  MergeBranchInput,
  ListRemotesInput,
  AddRemoteInput,
  RemoveRemoteInput,
  FetchRemoteInput,
  PullRemoteInput,
  PushRemoteInput,
  ListTagsInput,
  CreateTagInput,
  DeleteTagInput,
  PushTagsInput,
  ListStashesInput,
  StashPushInput,
  StashApplyInput,
  StashPopInput,
  StashDropInput,
  StashClearInput,
  ReadDiffInput,
  CompareRevisionsInput,
  GetBlameInput,
  ReadFileInput,
  GetConfigInput,
  SetConfigInput,
  ListWorktreesInput,
  AddWorktreeInput,
  RemoveWorktreeInput,
  GetReposInput,
  GetRepoInfoInput,
  ListSubmodulesInput,
  SubmoduleInitInput,
  SubmoduleUpdateInput,

  // Output types
  GetStatusOutput,
  StagePathsOutput,
  UnstagePathsOutput,
  AddPathsOutput,
  DiscardChangesOutput,
  RestorePathsOutput,
  RemovePathsOutput,
  CommitOutput,
  AmendCommitOutput,
  RevertCommitOutput,
  ResetRevisionOutput,
  QueryCommitOutput,
  QueryCommitsOutput,
  ListBranchesOutput,
  CreateBranchOutput,
  CheckoutBranchOutput,
  DeleteBranchOutput,
  MergeBranchOutput,
  ListRemotesOutput,
  AddRemoteOutput,
  RemoveRemoteOutput,
  FetchRemoteOutput,
  PullRemoteOutput,
  PushRemoteOutput,
  ListTagsOutput,
  CreateTagOutput,
  DeleteTagOutput,
  PushTagsOutput,
  ListStashesOutput,
  StashPushOutput,
  StashApplyOutput,
  StashPopOutput,
  StashDropOutput,
  StashClearOutput,
  ReadDiffOutput,
  CompareRevisionsOutput,
  GetBlameOutput,
  ReadFileOutput,
  GetConfigOutput,
  SetConfigOutput,
  ListWorktreesOutput,
  AddWorktreeOutput,
  RemoveWorktreeOutput,
  GetReposOutput,
  GetRepoInfoOutput,
  ListSubmodulesOutput,
  SubmoduleInitOutput,
  SubmoduleUpdateOutput,

  // Union helpers
  OperationInputs,
  OperationOutputs,
  GenericOpInput,
  GenericOpOutput,
  AnyOpInput,
  AnyOpOutput,
} from './operations.js';

export { OPERATION_MAP, ALL_OPERATION_NAMES } from './operations.js';