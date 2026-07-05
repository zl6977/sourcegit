import type { GitOperationHandler, HandlerContext } from '../registry.js'
import type { GitOperationHandler as H } from '../registry.js'

// Re-export helpers
export {
  parseStatusLine,
  parseStatusOutput,
  parseFileStats,
  parseCommits,
  parseDiffOutput,
  parseBranches,
  parseRemotes,
  parseTags,
  parseStashes,
  parseTreeOutput,
} from './helpers.js'

/* ─── status ────────────────────────────────────────────────────────── */
export { get_status, list_local_changes } from './status.js'

/* ─── add / stage ───────────────────────────────────────────────────── */
export { stage_paths, unstage_paths } from './add.js'

/* ─── commit ────────────────────────────────────────────────────────── */
export { commit, commit_amend } from './commit.js'

/* ─── commits / log ─────────────────────────────────────────────────── */
export { query_commits, query_commits_simple } from './commits.js'

/* ─── diff ───────────────────────────────────────────────────────────── */
export {
  read_diff,
  compare_revisions,
  query_commit,
  read_file_at,
} from './diff.js'

/* ─── branch ─────────────────────────────────────────────────────────── */
export {
  list_branches,
  create_branch,
  delete_branch,
  rename_branch,
} from './branch.js'

/* ─── checkout ───────────────────────────────────────────────────────── */
export {
  checkout_branch,
  checkout_file,
  checkout_detached,
  checkout_new_branch,
} from './checkout.js'

/* ─── remote ─────────────────────────────────────────────────────────── */
export {
  list_remotes,
  add_remote,
  remove_remote,
  fetch,
  pull,
  push,
} from './remote.js'

/* ─── stash ──────────────────────────────────────────────────────────── */
export {
  list_stashes,
  stash_changes,
  stash_apply,
  stash_drop,
  stash_pop,
} from './stash.js'

/* ─── file_ops ───────────────────────────────────────────────────────── */
export {
  reset_revision,
  restore_paths,
  discard_changes,
  remove_paths,
  move_paths,
} from './file_ops.js'

/* ─── tree / diff_tree ───────────────────────────────────────────────── */
export { read_tree, diff_tree } from './tree_ops.js'