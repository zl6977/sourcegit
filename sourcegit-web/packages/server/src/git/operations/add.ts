import type { GitOperationHandler, HandlerContext } from '../registry.js'

export const stage_paths: GitOperationHandler = async (ctx, input, repoPath) => {
  const paths = (input.paths as string[]) ?? ['.']
  for (const p of paths) {
    const result = await ctx.executor.execute({
      command: 'git',
      args: ['add', p],
      cwd: repoPath,
    })
    if (result.code !== 0) {
      return { success: false, error: `Failed to stage ${p}: ${result.stderr}` }
    }
  }
  return { success: true }
}

export const unstage_paths: GitOperationHandler = async (ctx, input, repoPath) => {
  const paths = (input.paths as string[]) ?? []
  if (paths.length === 0) {
    return { success: false, error: 'No paths provided' }
  }
  const result = await ctx.executor.execute({
    command: 'git',
    args: ['reset', 'HEAD', '--', ...paths],
    cwd: repoPath,
  })
  if (result.code !== 0) {
    return { success: false, error: `Failed to unstage: ${result.stderr}` }
  }
  return { success: true }
}