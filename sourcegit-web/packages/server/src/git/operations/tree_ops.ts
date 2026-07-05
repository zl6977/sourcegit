import type { GitOperationHandler, HandlerContext } from '../registry.js'
import { parseTreeOutput } from './helpers.js'

export const read_tree: GitOperationHandler = async (ctx, input, repoPath) => {
  const revision = (input.revision as string) ?? 'HEAD'

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['ls-tree', '-r', '--name-only', revision],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const files = parseTreeOutput(result.stdout)
  return { success: true, data: { files } }
}

export const diff_tree: GitOperationHandler = async (ctx, input, repoPath) => {
  const hash = input.hash as string

  if (!hash) {
    return { success: false, error: 'Commit hash is required' }
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args: ['diff-tree', '--no-commit-id', '-r', '--name-status', hash],
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  const files: Array<{ status: string; path: string }> = []
  for (const line of result.stdout.split('\n').filter(Boolean)) {
    const tabIdx = line.indexOf('\t')
    if (tabIdx < 0) continue
    const statusChar = line.substring(0, tabIdx)
    const path = line.substring(tabIdx + 1)
    const status = mapDiffStatus(statusChar)
    files.push({ status, path })
  }

  return { success: true, data: { files } }
}

function mapDiffStatus(char: string): string {
  switch (char) {
    case 'A': return 'added'
    case 'M': return 'modified'
    case 'D': return 'deleted'
    case 'R': return 'renamed'
    case 'C': return 'copied'
    case 'T': return 'type_changed'
    default: return 'modified'
  }
}