import type { Remote } from '@sourcegit/shared'
import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseRemotes } from './helpers.js'

export const list_remotes: GitOperationHandler = async (ctx, _input, repoPath) => {
  const result = await ctx.executor.execute({
    command: 'git',
    args: ['remote', '-v', '-z'],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const remotes = parseRemotes(result.stdout)
  return { success: true, data: { remotes } }
}

export const add_remote: GitOperationHandler = async (ctx, input, repoPath) => {
  const name = input.name as string
  const url = input.url as string

  if (!name || !url) {
    return { success: false, error: 'Remote name and URL are required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['remote', 'add', name, url],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { remote: { name, url } } }
}

export const remove_remote: GitOperationHandler = async (ctx, input, repoPath) => {
  const name = input.name as string

  if (!name) {
    return { success: false, error: 'Remote name is required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['remote', 'remove', name],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { remote: name } }
}

export const fetch: GitOperationHandler = async (ctx, input, repoPath) => {
  const name = input.name as string | undefined
  const args: string[] = []
  if (name) args.push(name)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['fetch', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const pull: GitOperationHandler = async (ctx, input, repoPath) => {
  const remote = input.remote as string | undefined
  const branch = input.branch as string | undefined
  const args: string[] = []
  if (remote) args.push(remote)
  if (branch) args.push(branch)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['pull', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const push: GitOperationHandler = async (ctx, input, repoPath) => {
  const remote = input.remote as string | undefined
  const branch = input.branch as string | undefined
  const force = !!input.force
  const args: string[] = []
  if (remote) args.push(remote)
  if (branch) args.push(branch)
  if (force) args.push('--force')

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['push', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}