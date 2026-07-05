import type { Diff, Commit, FileChange } from '@sourcegit/shared'
import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseDiffOutput, parseCommits } from './helpers.js'

export const read_diff: GitOperationHandler = async (ctx, input, repoPath) => {
  const staged = !!input.staged
  const filePath = input.path as string | undefined

  const args: string[] = staged ? ['--cached'] : []
  if (filePath) args.push('--', filePath)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['diff', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const diffs = parseDiffOutput(result.stdout)
  return { success: true, data: { diffs } }
}

export const compare_revisions: GitOperationHandler = async (
  ctx,
  input,
  repoPath,
) => {
  const from = input.from as string
  const to = input.to as string
  const filePath = input.path as string | undefined

  const args: string[] = [from, to]
  if (filePath) args.push('--', filePath)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['diff', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const diffs = parseDiffOutput(result.stdout)
  return { success: true, data: { diffs } }
}

export const query_commit: GitOperationHandler = async (ctx, input, repoPath) => {
  const hash = input.hash as string

  if (!hash) {
    return { success: false, error: 'Commit hash is required' }
  }

  // Get commit info
  const format =
    '%H%x1f%h%x1f%an%x1f%ae%x1f%ad%x1f%cn%x1f%ce%x1f%cd%x1f%P%x1f%s%x1f%B'
  const showResult = await ctx.executor.execute({
    command: 'git',
    args: ['show', `--format=${format}`, '--date=iso', '-s', hash],
    cwd: repoPath,
  })

  if (showResult.code !== 0) {
    return { success: false, error: showResult.stderr }
  }

  const commits = parseCommits(showResult.stdout)
  if (commits.length === 0) {
    return { success: false, error: 'Failed to parse commit' }
  }

  // Get files changed in this commit
  const diffTreeResult = await ctx.executor.execute({
    command: 'git',
    args: ['diff-tree', '--no-commit-id', '-r', '--name-status', hash],
    cwd: repoPath,
  })

  const files: FileChange[] = []
  if (diffTreeResult.code === 0) {
    for (const line of diffTreeResult.stdout.split('\n').filter(Boolean)) {
      const parts = line.split('\t')
      if (parts.length >= 2) {
        const statusChar = parts[0]
        const path = parts[1]
        const status = (statusChar === 'A' ? 'A' : statusChar === 'M' ? 'M' : statusChar === 'D' ? 'D' : statusChar === 'R' ? 'R' : statusChar === 'C' ? 'C' : 'M') as FileChange['status']
        files.push({ path, status, operation: ' ', newFile: statusChar === 'A' })
      }
    }
  }

  return { success: true, data: { commit: commits[0], files } }

}

export const read_file_at: GitOperationHandler = async (ctx, input, repoPath) => {
  const hash = input.hash as string
  const filePath = input.path as string

  if (!hash || !filePath) {
    return { success: false, error: 'Commit hash and file path are required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['show', `${hash}:${filePath}`],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { content: result.stdout, path: filePath } }
}