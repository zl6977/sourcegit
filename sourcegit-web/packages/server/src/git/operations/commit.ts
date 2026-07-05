import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import type { GitOperationHandler, HandlerContext } from '../registry.js'

export const commit: GitOperationHandler = async (ctx, input, repoPath) => {
  const message = input.message as string | undefined
  if (!message) {
    return { success: false, error: 'Commit message is required' }
  }

  // Write message to a temp file to handle multi-line safely
  const tmpDir = os.tmpdir()
  const tmpFile = path.join(tmpDir, `sourcegit-commit-msg-${Date.now()}.txt`)

  try {
    await fs.writeFile(tmpFile, message, 'utf8')

    const result = await ctx.executor.execute({
      command: 'git',
      args: ['commit', '-F', tmpFile],
      cwd: repoPath,
    })

    if (result.code !== 0) {
      return { success: false, error: result.stderr }
    }

    // Extract the new commit hash from output
    const hashMatch = result.stdout.match(/:[0-9a-f]{7}/)
    const hash = hashMatch ? hashMatch[1] : ''

    return { success: true, data: { hash } }
  } finally {
    try {
      await fs.unlink(tmpFile)
    } catch {
      // Ignore cleanup errors
    }
  }
}

export const commit_amend: GitOperationHandler = async (ctx, input, repoPath) => {
  const message = input.message as string | undefined
  const args: string[] = ['--amend']

  if (message) {
    // Use temp file for message
    const tmpDir = os.tmpdir()
    const tmpFile = path.join(tmpDir, `sourcegit-commit-amend-${Date.now()}.txt`)
    try {
      await fs.writeFile(tmpFile, message, 'utf8')
      args.push('-F', tmpFile)
    } finally {
      try {
        await fs.unlink(tmpFile)
      } catch {
        // Ignore
      }
    }
  } else {
    args.push('-C', 'HEAD')
  }

  const result = await ctx.executor.execute({
    command: 'git',
    args,
    cwd: repoPath,
  })

  if (result.code !== 0) {
    return { success: false, error: result.stderr }
  }

  return { success: true }
}