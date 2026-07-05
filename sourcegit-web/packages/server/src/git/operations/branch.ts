import type { Branch } from '@sourcegit/shared'
import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseBranches } from './helpers.js'

export const list_branches: GitOperationHandler = async (ctx, _input, repoPath) => {
  // Get current branch
  const currentResult = await ctx.executor.execute({
    command: 'git',
    args: ['symbolic-ref', '--short', 'HEAD'],
    cwd: repoPath,
  })

  const currentBranch = currentResult.code === 0 ? currentResult.stdout.trim() : ''

  // Get all branches
  const format = '%(refname:short)%x1f%(objectname)%x1f%(upstream:short)%x1f%(committerdate:iso)%x1f%(subject)'
  const result = await ctx.executor.execute({
    command: 'git',
    args: ['branch', '-a', `--format=${format}`, '-z'],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const branches = parseBranches(result.stdout, currentBranch)
  return { success: true, data: { branches, currentBranch } }
}

export const create_branch: GitOperationHandler = async (ctx, input, repoPath) => {
  const name = input.name as string
  const startPoint = input.startPoint as string | undefined

  if (!name) {
    return { success: false, error: 'Branch name is required' }
  }

  const args: string[] = [name]
  if (startPoint) args.push(startPoint)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['branch', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { branch: name } }
}

export const delete_branch: GitOperationHandler = async (ctx, input, repoPath) => {
  const name = input.name as string
  const force = !!input.force

  if (!name) {
    return { success: false, error: 'Branch name is required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['branch', force ? '-D' : '-d', name],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { branch: name } }
}

export const rename_branch: GitOperationHandler = async (ctx, input, repoPath) => {
  const newName = input.newName as string | undefined

  const args: string[] = ['-m']
  if (newName) args.push(newName)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['branch', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}