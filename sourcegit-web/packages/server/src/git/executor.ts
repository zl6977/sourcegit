import WebSocket from 'ws'
import type { GitBackend, GitCommandSpec, GitExecuteResult } from './backend.js'

export class CommandExecutor {
  constructor(private readonly backend: GitBackend) {}

  /**
   * Execute a single git command.
   */
  async execute(spec: GitCommandSpec): Promise<GitExecuteResult> {
    return this.backend.execute(spec)
  }

  /**
   * Execute a single git command and stream output to callbacks.
   */
  async streamExecute(
    spec: GitCommandSpec,
    onStdout: (chunk: string) => void,
    onStderr: (chunk: string) => void,
  ): Promise<GitExecuteResult> {
    return this.backend.streamExecute(spec, onStdout, onStderr)
  }

  /**
   * Execute a sequence of git commands, returning the results in order.
   * Aborts on first non-zero exit code (unless continueOnError).
   */
  async executeSequence(
    specs: GitCommandSpec[],
    inputStdin?: string,
  ): Promise<GitExecuteResult[]> {
    const results: GitExecuteResult[] = []
    for (const spec of specs) {
      const result = await this.execute(spec)
      if (result.code !== 0 && result.code !== null) {
        results.push(result)
        break
      }
      results.push(result)
    }
    return results
  }

  /**
   * Stream git command output to a WebSocket client.
   */
  async streamToWs(
    spec: GitCommandSpec,
    ws: WebSocket,
  ): Promise<GitExecuteResult> {
    return this.streamExecute(
      spec,
      (chunk) => {
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(chunk)
        }
      },
      (chunk) => {
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(`\u001b[31m${chunk}\u001b[0m`)
        }
      },
    )
  }
}