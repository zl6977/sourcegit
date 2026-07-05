import type { GitOperationHandler, HandlerContext } from '../registry.js'

export const checkout_branch: GitOperationHandler = async (ctx, input, repoPath) => {
  const branch = input.branch as string

  if (!branch) {
    return { success: false, error: 'Branch name is required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['checkout', branch],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { branch } }
}

export const checkout_detached: GitOperationHandler = async (ctx, input, repoPath) => {
  const commit = input.commit as string

  if (!commit) {
    return { success: false, error: 'Commit hash is required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['checkout', commit],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { commit } }
}

export const checkout_file: GitOperationHandler = async (ctx, input, repoPath) => {
  const filePath = input.path as string
  const commit = input.commit as string | undefined

  if (!filePath) {
    return { success: false, error: 'File path is required' }
  }

  const args: string[] = ['--']
  if (commit) args.unshift(commit)
  args.push(filePath)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['checkout', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { path: filePath } }
}

export const checkout_new_branch: GitOperationHandler = async (
  ctx,
  input,
  repoPath,
) => {
  const branch = input.branch as string
  const startPoint = input.startPoint as string | undefined

  if (!branch) {
    return { success: false, error: 'Branch name is required' }
  }

  const args: string[] = ['-b', branch]
  if (startPoint) args.push(startPoint)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['checkout', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true, data: { branch } }
}