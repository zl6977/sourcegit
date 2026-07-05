import type { StashEntry } from '@sourcegit/shared'
import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseStashes } from './helpers.js'

export const list_stashes: GitOperationHandler = async (ctx, input, repoPath) => {
  const maxCount = (input.maxCount as number) ?? 50

  const result = await ctx.executor.execute({
    command: 'git',
    args: [
      'stash',
      'list',
      '-n',
      String(maxCount),
      '--format=%gd%x1f%(objectname)%x1f%s',
    ],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const stashes = parseStashes(result.stdout)
  return { success: true, data: { stashes } }
}

export const stash_changes: GitOperationHandler = async (ctx, input, repoPath) => {
  const message = input.message as string | undefined
  const includeUntracked = !!input.includeUntracked

  const args: string[] = []
  if (includeUntracked) args.push('-u')
  if (message) args.push('-m', message)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['stash', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const stash_apply: GitOperationHandler = async (ctx, input, repoPath) => {
  const index = input.index as string | undefined

  const target = index !== undefined ? index : 'stash@{0}'

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['stash', 'apply', target],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const stash_drop: GitOperationHandler = async (ctx, input, repoPath) => {
  const index = input.index as string | undefined

  const target = index !== undefined ? index : 'stash@{0}'

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['stash', 'drop', target],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const stash_pop: GitOperationHandler = async (ctx, input, repoPath) => {
  const index = input.index as string | undefined

  const target = index !== undefined ? index : 'stash@{0}'

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['stash', 'pop', target],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}