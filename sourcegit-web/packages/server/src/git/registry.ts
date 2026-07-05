import type { CommandExecutor } from './executor.js'

export interface HandlerContext {
  executor: CommandExecutor
}

export interface OpResult {
  success: boolean
  error?: string
  data?: unknown
}

export interface GitOperationHandler {
  (ctx: HandlerContext, input: Record<string, unknown>, repoPath: string): Promise<OpResult>
}

export class GitOperationRegistry {
  private readonly handlers = new Map<string, GitOperationHandler>()

  register(name: string, handler: GitOperationHandler): void {
    this.handlers.set(name, handler)
  }

  has(name: string): boolean {
    return this.handlers.has(name)
  }

  list(): string[] {
    return [...this.handlers.keys()]
  }

  registerMany(handlers: Record<string, GitOperationHandler>): void {
    for (const [name, handler] of Object.entries(handlers)) {
      this.register(name, handler)
    }
  }

  async dispatch(
    name: string,
    repoPath: string,
    ctx: HandlerContext,
    input?: Record<string, unknown>,
  ): Promise<OpResult> {
    const handler = this.handlers.get(name)
    if (!handler) {
      return { success: false, error: `Unknown operation: ${name}` }
    }
    try {
      return await handler(ctx, input ?? {}, repoPath)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      return { success: false, error: message }
    }
  }
}