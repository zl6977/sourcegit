import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseStatusOutput } from './helpers.js'

export const get_status: GitOperationHandler = async (_ctx, _input, repoPath) => {
  // get_status is handled via the translator+executor flow,
  // so this handler is a no-op — the actual call is in translator.ts
  return { success: true, data: null }
}

export const list_local_changes: GitOperationHandler = async (ctx, _input, repoPath) => {
  const result = await ctx.executor.execute({
    command: 'git',
    args: ['status', '--porcelain=v1', '-z'],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const parsed = parseStatusOutput(result.stdout)
  return {
    success: true,
    data: {
      ...parsed,
      count: parsed.staged.length + parsed.unstaged.length + parsed.untracked.length,
    },
  }
}