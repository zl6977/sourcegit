import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseCommits } from './helpers.js'

export const query_commits: GitOperationHandler = async (ctx, input, repoPath) => {
  const branch = input.branch as string | undefined
  const maxCount = (input.maxCount as number) ?? 500

  const format =
    '%x1f%H%x1f%h%x1f%an%x1f%ae%x1f%ad%x1f%cn%x1f%ce%x1f%cd%x1f%P%x1f%T%x1f%B'
  const args: string[] = [
    '--graph',
    '-n',
    String(maxCount),
    '--format=' + format,
    '--date=iso',
  ]
  if (branch) args.push(branch)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['log', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const commits = parseCommits(result.stdout)

  return {
    success: true,
    data: {
      commits,
      graphRaw: result.stdout,
    },
  }
}

export const query_commits_simple: GitOperationHandler = async (ctx, input, repoPath) => {
  const branch = input.branch as string | undefined
  const maxCount = (input.maxCount as number) ?? 100

  const format = '%H%x1f%h%x1f%an%x1f%ae%x1f%ad%x1f%s'
  const args: string[] = ['-n', String(maxCount), '--format=' + format, '--date=iso']
  if (branch) args.push(branch)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['log', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const commits = parseCommits(result.stdout)
  return { success: true, data: { commits } }
}