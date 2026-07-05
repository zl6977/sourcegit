import type { GitOperationHandler, HandlerContext } from '../registry.js'

export const reset_revision: GitOperationHandler = async (ctx, input, repoPath) => {
  const target = input.target as string | undefined
  const mode = input.mode as string | undefined

  const args: string[] = []
  if (mode) args.push(mode)
  if (target) args.push(target)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['reset', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const restore_paths: GitOperationHandler = async (ctx, input, repoPath) => {
  const paths = (input.paths as string[]) ?? []
  const staged = !!input.staged
  const source = input.source as string | undefined

  if (paths.length === 0) {
    return { success: false, error: 'No paths provided' }
  }

  const args: string[] = []
  if (staged) args.push('--staged')
  else args.push('--worktree')
  if (source) args.push('--source', source)
  args.push('--', ...paths)

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['restore', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const discard_changes: GitOperationHandler = async (
  ctx,
  input,
  repoPath,
) => {
  const paths = (input.paths as string[]) ?? []
  const staged = !!input.staged

  if (paths.length === 0) {
    // Discard all changes
    const result = await ctx.executor.execute({
      command: 'git',
      args: ['checkout', '--', '.'],
      cwd: repoPath,
    })
    if (result.code !== 0) {
      return { success: false, error: result.stderr }
    }
    return { success: true }
  }

  const args: string[] = ['--']
  if (staged) {
    // Unstage then restore
    const resetResult = await ctx.executor.execute({
      command: 'git',
      args: ['reset', 'HEAD', '--', ...paths],
      cwd: repoPath,
    })
    if (resetResult.code !== 0) {
      return { success: false, error: resetResult.stderr }
    }
  }

  args.push(...paths)
  const result = await ctx.executor.execute({
    command: 'git',
    args: ['checkout', ...args],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}

export const remove_paths: GitOperationHandler = async (ctx, input, repoPath) => {
  const paths = (input.paths as string[]) ?? []

  if (paths.length === 0) {
    return { success: false, error: 'No paths provided' }
  }

  for (const p of paths) {
    const result = await ctx.executor.execute({
      command: 'git',
      args: ['rm', p],
      cwd: repoPath,
    })
    if (result.code !== 0) {
      return { success: false, error: `Failed to remove ${p}: ${result.stderr}` }
    }
  }

  return { success: true }
}
export const move_paths: GitOperationHandler = async (ctx, input, repoPath) => {
  const from = input.from as string
  const to = input.to as string

  if (!from || !to) {
    return { success: false, error: 'From and to paths are required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['mv', from, to],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}