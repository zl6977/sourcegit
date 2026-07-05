/**
 * Operation type definitions for the SourceGit Web GUI.
 *
 * Every git operation exposed over the WebSocket has an input interface
 * (what the client sends) and an output interface (what the server
 * returns).  The `OPERATION_MAP` provides the canonical name→descriptor
 * mapping used by the client and server to agree on semantics.
 */
/* ─────────────────────────────────────────────
 *  Operation Map
 * ───────────────────────────────────────────── */
export const OPERATION_MAP = {
    get_status: { name: 'get_status', description: 'Get current repository status', category: 'status' },
    stage_paths: { name: 'stage_paths', description: 'Stage specified file paths', category: 'working_copy' },
    unstage_paths: { name: 'unstage_paths', description: 'Unstage specified file paths', category: 'working_copy' },
    add_paths: { name: 'add_paths', description: 'Add untracked files to the index', category: 'working_copy' },
    discard_changes: { name: 'discard_changes', description: 'Discard working-tree changes', category: 'working_copy' },
    restore_paths: { name: 'restore_paths', description: 'Restore files to a revision', category: 'working_copy' },
    remove_paths: { name: 'remove_paths', description: 'Remove files (rm/rm --cached)', category: 'working_copy' },
    commit: { name: 'commit', description: 'Create a new commit', category: 'commit' },
    amend_commit: { name: 'amend_commit', description: 'Amend the latest commit', category: 'commit' },
    revert_commit: { name: 'revert_commit', description: 'Revert a commit', category: 'commit' },
    reset_revision: { name: 'reset_revision', description: 'Reset HEAD to a revision', category: 'commit' },
    query_commit: { name: 'query_commit', description: 'Query a single commit by revision', category: 'commit' },
    query_commits: { name: 'query_commits', description: 'Query commit history with options', category: 'commit' },
    list_branches: { name: 'list_branches', description: 'List branches (local and/or remote)', category: 'branch' },
    create_branch: { name: 'create_branch', description: 'Create a new branch', category: 'branch' },
    checkout_branch: { name: 'checkout_branch', description: 'Checkout a branch', category: 'branch' },
    delete_branch: { name: 'delete_branch', description: 'Delete a branch', category: 'branch' },
    merge_branch: { name: 'merge_branch', description: 'Merge a branch into current HEAD', category: 'branch' },
    list_remotes: { name: 'list_remotes', description: 'List configured remotes', category: 'remote' },
    add_remote: { name: 'add_remote', description: 'Add a remote', category: 'remote' },
    remove_remote: { name: 'remove_remote', description: 'Remove a remote', category: 'remote' },
    fetch_remote: { name: 'fetch_remote', description: 'Fetch from a remote', category: 'remote' },
    pull_remote: { name: 'pull_remote', description: 'Pull from a remote', category: 'remote' },
    push_remote: { name: 'push_remote', description: 'Push to a remote', category: 'remote' },
    list_tags: { name: 'list_tags', description: 'List tags', category: 'tag' },
    create_tag: { name: 'create_tag', description: 'Create a tag', category: 'tag' },
    delete_tag: { name: 'delete_tag', description: 'Delete a tag', category: 'tag' },
    push_tags: { name: 'push_tags', description: 'Push tags to a remote', category: 'tag' },
    list_stashes: { name: 'list_stashes', description: 'List stash entries', category: 'stash' },
    stash_push: { name: 'stash_push', description: 'Push changes to the stash', category: 'stash' },
    stash_apply: { name: 'stash_apply', description: 'Apply a stash entry', category: 'stash' },
    stash_pop: { name: 'stash_pop', description: 'Pop and apply a stash entry', category: 'stash' },
    stash_drop: { name: 'stash_drop', description: 'Drop a stash entry', category: 'stash' },
    stash_clear: { name: 'stash_clear', description: 'Clear all stashes', category: 'stash' },
    read_diff: { name: 'read_diff', description: 'Read diff for a file or revision', category: 'diff' },
    compare_revisions: { name: 'compare_revisions', description: 'Compare two revisions', category: 'diff' },
    get_blame: { name: 'get_blame', description: 'Get blame info for a file', category: 'diff' },
    read_file: { name: 'read_file', description: 'Read file content at a revision', category: 'diff' },
    get_config: { name: 'get_config', description: 'Get git config entries', category: 'config' },
    set_config: { name: 'set_config', description: 'Set a git config value', category: 'config' },
    list_worktrees: { name: 'list_worktrees', description: 'List worktrees', category: 'worktree' },
    add_worktree: { name: 'add_worktree', description: 'Add a worktree', category: 'worktree' },
    remove_worktree: { name: 'remove_worktree', description: 'Remove a worktree', category: 'worktree' },
    get_repos: { name: 'get_repos', description: 'Discover git repositories', category: 'status' },
    get_repo_info: { name: 'get_repo_info', description: 'Get repository metadata', category: 'status' },
    list_submodules: { name: 'list_submodules', description: 'List submodules', category: 'status' },
    submodule_init: { name: 'submodule_init', description: 'Initialize submodules', category: 'status' },
    submodule_update: { name: 'submodule_update', description: 'Update submodules', category: 'status' },
};
/** Ordered array of all operation names. */
export const ALL_OPERATION_NAMES = Object.keys(OPERATION_MAP);
//# sourceMappingURL=operations.js.map