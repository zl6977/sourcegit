import express, { Request, Response } from 'express'
interface LocalGitOperation { name: string; input?: Record<string, unknown> }
interface LocalOpResult { success: boolean; error?: string; data?: unknown }
import { detectBackend, closeAllBackends } from './git/backend.js'
import { CommandExecutor } from './git/executor.js'
import { GitOperationRegistry, type HandlerContext } from './git/registry.js'
import * as handlers from './git/operations/index.js'

const router = express.Router()

/* ─── Repo storage ──────────────────────────────────────────────────── */

const repos = new Map<string, string>()   // path → name
const repoNames = new Map<string, string>() // name → path

interface RepoRecord {
  name: string
  path: string
  backend: ReturnType<typeof detectBackend>
  executor: CommandExecutor
  registry: GitOperationRegistry
}

const repoRecords = new Map<string, RepoRecord>() // path → record

/**
 * Resolve a repo by name or path. Tries name→path lookup first.
 */
function resolveRepoParam(id: string): RepoRecord | null {
  // Try name lookup first
  const path = repoNames.get(id)
  if (path) return repoRecords.get(path) ?? null

  // Try path lookup
  return repoRecords.get(id) ?? null
}

/* ─── Initialize registry ───────────────────────────────────────────── */

function createRegistry(): GitOperationRegistry {
  const registry = new GitOperationRegistry()
  registry.register('status', handlers.get_status)
  registry.register('list_local_changes', handlers.list_local_changes)
  registry.register('add', handlers.stage_paths)
  registry.register('file_stage', handlers.stage_paths)
  registry.register('file_unstage', handlers.unstage_paths)
  registry.register('commit', handlers.commit)
  registry.register('commit_amend', handlers.commit_amend)
  registry.register('commits', handlers.query_commits)
  registry.register('log', handlers.query_commits_simple)
  registry.register('diff', handlers.read_diff)
  registry.register('compare_revisions', handlers.compare_revisions)
  registry.register('query_commit', handlers.query_commit)
  registry.register('read_file_at', handlers.read_file_at)
  registry.register('branch_list', handlers.list_branches)
  registry.register('branch_create', handlers.create_branch)
  registry.register('branch_delete', handlers.delete_branch)
  registry.register('branch_rename', handlers.rename_branch)
  registry.register('checkout', handlers.checkout_branch)
  registry.register('checkout_file', handlers.checkout_file)
  registry.register('checkout_detached', handlers.checkout_detached)
  registry.register('checkout_new_branch', handlers.checkout_new_branch)
  registry.register('reset', handlers.reset_revision)
  registry.register('reset_soft', handlers.reset_revision)
  registry.register('remote_list', handlers.list_remotes)
  registry.register('remote_add', handlers.add_remote)
  registry.register('remote_remove', handlers.remove_remote)
  registry.register('remote_fetch', handlers.fetch)
  registry.register('remote_push', handlers.push)
  registry.register('stash_list', handlers.list_stashes)
  registry.register('stash_push', handlers.stash_changes)
  registry.register('stash_apply', handlers.stash_apply)
  registry.register('stash_drop', handlers.stash_drop)
  registry.register('stash_pop', handlers.stash_pop)
  registry.register('file_delete', handlers.remove_paths)
  registry.register('file_move', handlers.move_paths)
  registry.register('file_ops', handlers.restore_paths)
  registry.register('tree', handlers.read_tree)
  registry.register('diff_tree', handlers.diff_tree)
  return registry
}

/* ─── Register repo ─────────────────────────────────────────────────── */

function registerRepo(name: string, path: string): RepoRecord {
  const backend = detectBackend(path)
  const executor = new CommandExecutor(backend)
  const registry = createRegistry()
  const record: RepoRecord = { name, path, backend, executor, registry }

  repos.set(path, name)
  repoNames.set(name, path)
  repoRecords.set(path, record)

  return record
}

/* ─── Routes ────────────────────────────────────────────────────────── */

/** POST / — Register a repository */
router.post('/', (_req: Request, res: Response) => {
  const { name, path: repoPath } = _req.body
  if (!name || !repoPath) {
    res.status(400).json({ error: 'name and path are required' })
    return
  }

  try {
    const record = registerRepo(name, repoPath)
    res.json({ success: true, name: record.name, path: record.path })
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET / — List registered repositories */
router.get('/', (_req: Request, res: Response) => {
  const entries: Array<{ name: string; path: string }> = []
  for (const [path, name] of repos) {
    entries.push({ name, path })
  }
  res.json({ repos: entries })
})

/** GET /:id/info — Repository info */
router.get('/:id/info', (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }
  res.json({ name: record.name, path: record.path })
})

/** GET /:id/status — Repository status */
router.get('/:id/status', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    // Get current branch
    const branchResult = await record.executor.execute({
      command: 'git',
      args: ['symbolic-ref', '--short', 'HEAD'],
      cwd: record.path,
    })
    const currentBranch = branchResult.code === 0 ? branchResult.stdout.trim() : 'HEAD'

    // Get file status
    const statusResult = await record.executor.execute({
      command: 'git',
      args: ['status', '--porcelain=v1', '-z'],
      cwd: record.path,
    })

    const { parseStatusOutput } = await import('./git/operations/helpers.js')
    const parsed = parseStatusOutput(statusResult.stdout)

    const isClean =
      parsed.staged.length === 0 &&
      parsed.unstaged.length === 0 &&
      parsed.untracked.length === 0

    res.json({
      isClean,
      currentBranch,
      staged: parsed.staged,
      unstaged: parsed.unstaged,
      untracked: parsed.untracked,
    })
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/commits — Commit history */
router.get('/:id/commits', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const branch = (_req.query.branch as string) || undefined
    const maxCount = parseInt(String(_req.query.maxCount ?? '500'), 10)

    const result = await record.registry.dispatch(
      'commits',
      record.path,
      { executor: record.executor },
      { branch, maxCount },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/branches — Branch list */
router.get('/:id/branches', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }
  try {
    const result = await record.registry.dispatch(
      'branch_list',
      record.path,
      { executor: record.executor },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/remotes — Remote list */
router.get('/:id/remotes', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const result = await record.registry.dispatch(
      'remote_list',
      record.path,
      { executor: record.executor },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/tags — Tag list */
router.get('/:id/tags', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const gitResult = await record.executor.execute({
      command: 'git',
      args: ['tag', '-l', '-n1', '-z'],
      cwd: record.path,
    })

    const { parseTags } = await import('./git/operations/helpers.js')
    const tags = parseTags(gitResult.stdout)
    res.json({ tags })
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/stashes — Stash list */
router.get('/:id/stashes', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const result = await record.registry.dispatch(
      'stash_list',
      record.path,
      { executor: record.executor },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/diff — File diff */
router.get('/:id/diff', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const staged = !!_req.query.staged
    const path = _req.query.path as string | undefined

    const result = await record.registry.dispatch(
      'diff',
      record.path,
      { executor: record.executor },
      { staged, path },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** GET /:id/tree — File tree */
router.get('/:id/tree', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  try {
    const revision = (_req.query.revision as string) || 'HEAD'

    const result = await record.registry.dispatch(
      'tree',
      record.path,
      { executor: record.executor },
      { revision },
    )

    if (!result.success) {
      res.status(500).json({ error: result.error })
      return
    }

    res.json(result.data)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

/** POST /:id/operations — Execute a git operation */
router.post('/:id/operations', async (_req: Request, res: Response) => {
  const record = resolveRepoParam(decodeURIComponent(_req.params.id))
  if (!record) {
    res.status(404).json({ error: 'Repository not found' })
    return
  }

  const op: LocalGitOperation = _req.body
  if (!op || !op.name) {
    res.status(400).json({ error: 'Operation name is required' })
    return
  }

  try {
    const result = await record.registry.dispatch(
      op.name,
      record.path,
      { executor: record.executor },
      op.input,
    )

    if (!result.success) {
      res.status(500).json({ success: false, error: result.error })
      return
    }

    res.json(result)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    res.status(500).json({ error: msg })
  }
})

export { router, repos, repoNames, closeAllBackends, registerRepo, resolveRepoParam }