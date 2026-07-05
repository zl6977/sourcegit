import type { GitCommandSpec } from './backend.js'

/* ─── Translator ────────────────────────────────────────────────────── */

export type GitTranslatorFn = (input: Record<string, unknown>, repoPath: string) => GitCommandSpec[]

export class GitCliTranslator {
  private readonly map = new Map<string, GitTranslatorFn>()

  constructor() {
    this.registerBuiltins()
  }

  private registerBuiltins(): void {
    // ── Status ──────────────────────────────────────────────────────
    this.map.set('status', (_, repoPath) => [
      this.spec('status', ['--porcelain=v1', '-z'], repoPath),
    ])

    this.map.set('list_local_changes', (_, repoPath) => [
      this.spec('status', ['-s', '-z'], repoPath),
    ])

    // ── Add / Stage ─────────────────────────────────────────────────
    this.map.set('add', (input, repoPath) => {
      const paths = (input.paths as string[]) ?? ['.']
      return paths.map((p) => this.spec('add', [p], repoPath))
    })

    this.map.set('file_stage', (input, repoPath) => {
      const paths = (input.paths as string[]) ?? []
      return paths.map((p) => this.spec('add', [p], repoPath))
    })

    this.map.set('file_unstage', (input, repoPath) => {
      const paths = (input.paths as string[]) ?? []
      return paths.map((p) => this.spec('reset', ['HEAD', '--', p], repoPath))
    })

    // ── Commit ──────────────────────────────────────────────────────
    this.map.set('commit', (input, repoPath) => {
      const message = input.message as string
      const all = !!input.all
      const args: string[] = []
      if (all) args.push('-a')
      args.push('-F')
      args.push(`< /dev/stdin`)
      // We pass the message via stdin in the handler
      return [this.spec('commit', args, repoPath)]
    })

    this.map.set('commit_amend', (input, repoPath) => {
      const message = input.message as string
      const args: string[] = ['--amend']
      if (message) {
        args.push('-m', message)
      } else {
        args.push('-C', 'HEAD')
      }
      return [this.spec('commit', args, repoPath)]
    })

    // ── Commits / Log ───────────────────────────────────────────────
    this.map.set('commits', (input, repoPath) => {
      const branch = input.branch as string | undefined
      const maxCount = (input.maxCount as number) ?? 500
      const format = '%x1F%H%x1F%h%x1F%an%x1F%ae%x1F%ad%x1F%cn%x1F%ce%x1F%cd%x1F%P%x1F%T%x1F%B'
      const args: string[] = [
        '--graph',
        '-n', String(maxCount),
        '--format=' + format,
        '--date=iso',
      ]
      if (branch) args.push(branch)
      return [this.spec('log', args, repoPath)]
    })

    this.map.set('log', (input, repoPath) => {
      const branch = input.branch as string | undefined
      const maxCount = (input.maxCount as number) ?? 100
      const format = '%H%x1F%h%x1F%an%x1F%ae%x1F%ad%x1F%s'
      const args: string[] = [
        '-n', String(maxCount),
        '--format=' + format,
        '--date=iso',
      ]
      if (branch) args.push(branch)
      return [this.spec('log', args, repoPath)]
    })

    // ── Diff ────────────────────────────────────────────────────────
    this.map.set('diff', (input, repoPath) => {
      const staged = !!input.staged
      const path = input.path as string | undefined
      const args: string[] = staged ? ['--cached'] : []
      if (path) args.push('--', path)
      return [this.spec('diff', args, repoPath)]
    })

    // ── Branch ──────────────────────────────────────────────────────
    this.map.set('branch_list', (_, repoPath) => [
      this.spec('branch', ['--list', '--format=%(refname:short)%x1F%(objectname)%x1F%(upstream:short)%x1F%(committerdate:iso)%x1F%(subject)', '-z'], repoPath),
    ])

    this.map.set('branch_create', (input, repoPath) => {
      const name = input.name as string
      const startPoint = input.startPoint as string | undefined
      const args: string[] = [name]
      if (startPoint) args.push(startPoint)
      return [this.spec('branch', args, repoPath)]
    })

    this.map.set('branch_delete', (input, repoPath) => {
      const name = input.name as string
      const force = !!input.force
      return [this.spec('branch', [force ? '-D' : '-d', name], repoPath)]
    })

    this.map.set('branch_rename', (input, repoPath) => {
      const newName = input.newName as string
      const args: string[] = ['-m']
      if (newName) args.push(newName)
      return [this.spec('branch', args, repoPath)]
    })

    // ── Checkout ────────────────────────────────────────────────────
    this.map.set('checkout', (input, repoPath) => {
      const branch = input.branch as string
      const args: string[] = [branch]
      if (input.force as boolean) args.unshift('-f')
      return [this.spec('checkout', args, repoPath)]
    })

    this.map.set('checkout_file', (input, repoPath) => {
      const path = input.path as string
      const commit = input.commit as string | undefined
      const args: string[] = ['--']
      if (commit) args.unshift(commit)
      args.push(path)
      return [this.spec('checkout', args, repoPath)]
    })

    // ── Reset ───────────────────────────────────────────────────────
    this.map.set('reset', (input, repoPath) => {
      const target = input.target as string ?? 'HEAD'
      const mode = input.mode as string ?? 'mixed'
      return [this.spec('reset', [mode, target], repoPath)]
    })

    this.map.set('reset_soft', (input, repoPath) => {
      const target = input.target as string ?? 'HEAD'
      return [this.spec('reset', ['--soft', target], repoPath)]
    })

    // ── Remote ──────────────────────────────────────────────────────
    this.map.set('remote_list', (_, repoPath) => [
      this.spec('remote', ['-v', '-z'], repoPath),
    ])

    this.map.set('remote_add', (input, repoPath) => {
      const name = input.name as string
      const url = input.url as string
      return [this.spec('remote', ['add', name, url], repoPath)]
    })

    this.map.set('remote_remove', (input, repoPath) => {
      const name = input.name as string
      return [this.spec('remote', ['remove', name], repoPath)]
    })

    this.map.set('remote_fetch', (input, repoPath) => {
      const name = input.name as string | undefined
      const args: string[] = []
      if (name) args.push(name)
      return [this.spec('fetch', args, repoPath)]
    })

    this.map.set('remote_push', (input, repoPath) => {
      const remote = input.remote as string ?? 'origin'
      const branch = input.branch as string | undefined
      const force = !!input.force
      const args: string[] = [remote]
      if (branch) args.push(branch)
      if (force) args.push('-f')
      return [this.spec('push', args, repoPath)]
    })

    // ── Stash ───────────────────────────────────────────────────────
    this.map.set('stash_list', (input, repoPath) => {
      const maxCount = (input.maxCount as number) ?? 50
      return [this.spec('stash', ['list', '-n', String(maxCount), '--format=%gd%x1F%d%x1F%s'], repoPath)]
    })

    this.map.set('stash_push', (input, repoPath) => {
      const message = input.message as string | undefined
      const includeUntracked = !!input.includeUntracked
      const args: string[] = []
      if (includeUntracked) args.push('-u')
      if (message) args.push('-m', message)
      return [this.spec('stash', args, repoPath)]
    })

    this.map.set('stash_apply', (input, repoPath) => {
      const index = input.index as string ?? 'stash@{0}'
      return [this.spec('stash', ['apply', index], repoPath)]
    })

    this.map.set('stash_drop', (input, repoPath) => {
      const index = input.index as string ?? 'stash@{0}'
      return [this.spec('stash', ['drop', index], repoPath)]
    })

    // ── File ops ────────────────────────────────────────────────────
    this.map.set('file_delete', (input, repoPath) => {
      const paths = (input.paths as string[]) ?? []
      return paths.map((p) => this.spec('rm', [p], repoPath))
    })

    this.map.set('file_move', (input, repoPath) => {
      const from = input.from as string
      const to = input.to as string
      return [this.spec('mv', [from, to], repoPath)]
    })

    // ── Tree ────────────────────────────────────────────────────────
    this.map.set('tree', (input, repoPath) => {
      const revision = input.revision as string ?? 'HEAD'
      return [this.spec('ls-tree', ['-r', '--name-only', revision], repoPath)]
    })

    // ── Additional: tags ────────────────────────────────────────────
    this.map.set('tag_list', (_, repoPath) => [
      this.spec('tag', ['-l', '-n1', '-z'], repoPath),
    ])

    this.map.set('tag_create', (input, repoPath) => {
      const name = input.name as string
      const message = input.message as string | undefined
      const args: string[] = ['-a', name, '-m', message ?? name]
      return [this.spec('tag', args, repoPath)]
    })

    this.map.set('tag_delete', (input, repoPath) => {
      const name = input.name as string
      return [this.spec('tag', ['-d', name], repoPath)]
    })

    // ── Additional: worktree ────────────────────────────────────────
    this.map.set('worktree_list', (_, repoPath) => [
      this.spec('worktree', ['list', '--porcelain'], repoPath),
    ])

    this.map.set('worktree_add', (input, repoPath) => {
      const path = input.path as string
      const branch = input.branch as string
      return [this.spec('worktree', ['add', path, branch], repoPath)]
    })

    this.map.set('worktree_remove', (input, repoPath) => {
      const path = input.path as string
      const force = !!input.force
      const args: string[] = [path]
      if (force) args.push('-f')
      return [this.spec('worktree', ['remove', ...args], repoPath)]
    })

    // ── Additional: merge ───────────────────────────────────────────
    this.map.set('merge', (input, repoPath) => {
      const branch = input.branch as string
      const noFastForward = !!input.noFastForward
      const args: string[] = []
      if (noFastForward) args.push('--no-ff')
      args.push(branch)
      return [this.spec('merge', args, repoPath)]
    })

    // ── Additional: rebase ──────────────────────────────────────────
    this.map.set('rebase', (input, repoPath) => {
      const onto = input.onto as string | undefined
      const args: string[] = []
      if (onto) args.push(onto)
      return [this.spec('rebase', args, repoPath)]
    })

    // ── Additional: cherry-pick ─────────────────────────────────────
    this.map.set('cherry_pick', (input, repoPath) => {
      const commit = input.commit as string
      return [this.spec('cherry-pick', [commit], repoPath)]
    })

    // ── Additional: revert ──────────────────────────────────────────
    this.map.set('revert', (input, repoPath) => {
      const commit = input.commit as string
      return [this.spec('revert', [commit], repoPath)]
    })

    // ── Additional: blame ───────────────────────────────────────────
    this.map.set('blame', (input, repoPath) => {
      const file = input.file as string
      const revision = input.revision as string | undefined
      const args: string[] = []
      if (revision) args.push(revision)
      args.push('--', file)
      return [this.spec('blame', args, repoPath)]
    })

    // ── Additional: show (single commit details) ────────────────────
    this.map.set('show', (input, repoPath) => {
      const hash = input.hash as string
      const format = '%H%x1F%h%x1F%an%x1F%ae%x1F%ad%x1F%cn%x1F%ce%x1F%cd%x1F%P%x1F%s%x1F%b'
      return [this.spec('show', [`--format=${format}`, '--date=iso', hash], repoPath)]
    })

    // ── Additional: diff-tree (files changed in a commit) ───────────
    this.map.set('diff_tree', (input, repoPath) => {
      const hash = input.hash as string
      return [this.spec('diff-tree', ['--no-commit-id', '-r', '--name-status', hash], repoPath)]
    })

    // ── Additional: rev-parse ───────────────────────────────────────
    this.map.set('rev_parse', (input, repoPath) => {
      const ref = input.ref as string ?? 'HEAD'
      return [this.spec('rev-parse', [ref], repoPath)]
    })

    // ── Additional: current branch ──────────────────────────────────
    this.map.set('current_branch', (_, repoPath) => [
      this.spec('symbolic-ref', ['--short', 'HEAD'], repoPath),
    ])

    // ── Additional: HEAD info ───────────────────────────────────────
    this.map.set('head_info', (_, repoPath) => [
      this.spec('rev-parse', ['HEAD'], repoPath),
    ])

    // ── Additional: is-clean ────────────────────────────────────────
    this.map.set('is_clean', (_, repoPath) => [
      this.spec('status', ['--porcelain'], repoPath),
    ])

    // ── Additional: ahead/behind ────────────────────────────────────
    this.map.set('ahead_behind', (_, repoPath) => {
      return [
        this.spec('rev-parse', ['--abbrev-ref', 'HEAD'], repoPath),
        this.spec('rev-parse', ['--abbrev-ref', '@{u}'], repoPath),
      ]
    })

    // ── Additional: submodules ──────────────────────────────────────
    this.map.set('submodule_list', (_, repoPath) => [
      this.spec('submodule', ['status', '--recursive', '-z'], repoPath),
    ])

    // ── Additional: config ──────────────────────────────────────────
    this.map.set('config_get', (input, repoPath) => {
      const key = input.key as string
      return [this.spec('config', [key], repoPath)]
    })

    this.map.set('config_set', (input, repoPath) => {
      const key = input.key as string
      const value = input.value as string
      return [this.spec('config', [key, value], repoPath)]
    })
  }

  /** Get translator function for an operation name. Returns undefined if not found. */
  get(name: string): GitTranslatorFn | undefined {
    return this.map.get(name)
  }

  /** Check if an operation has a translator. */
  has(name: string): boolean {
    return this.map.has(name)
  }

  /** List all operation names that have translators. */
  list(): string[] {
    return [...this.map.keys()]
  }

  private spec(command: string, args: string[], cwd: string): GitCommandSpec {
    return { command, args, cwd }
  }
}