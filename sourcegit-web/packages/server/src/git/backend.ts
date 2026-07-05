import { spawn, ChildProcess, StdioOptions } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { isWslPath, getDistroFromWslPath, parseSshPath } from '../utils/paths.js'

/* ─── Types ─────────────────────────────────────────────────────────── */

export interface GitCommandSpec {
  command: string
  args: string[]
  cwd?: string
}

export interface GitExecuteResult {
  stdout: string
  stderr: string
  code: number | null
}

/* ─── Abstract backend ──────────────────────────────────────────────── */

export abstract class GitBackend {
  public abstract label: string

  public abstract get gitBinary(): string

  /** Run a single git command and collect its output. */
  public abstract execute(spec: GitCommandSpec): Promise<GitExecuteResult>

  /**
   * Stream the stdout/stderr of a git command to a writable stream (e.g. WS).
   * Resolves when the process exits.
   */
  public abstract streamExecute(
    spec: GitCommandSpec,
    onStdout: (chunk: string) => void,
    onStderr: (chunk: string) => void,
  ): Promise<GitExecuteResult>

  /** Build a GitCommandSpec from pieces. Sub-classes may override to inject flags. */
  public buildCommand(subCommand: string, args: string[], cwd?: string): GitCommandSpec {
    return { command: this.gitBinary, args: [subCommand, ...args], cwd }
  }

  public abstract close(): void
}

/* ─── NativeGitBackend ──────────────────────────────────────────────── */

const OUTPUT_LIMIT = 10 * 1024 * 1024 // 10 MB

export class NativeGitBackend extends GitBackend {
  public label = 'native'
  public gitBinary = 'git'

  private _gitPath: string | null = null

  public get gitBinaryResolved(): string {
    return this._gitPath ?? this.gitBinary
  }

  async execute(spec: GitCommandSpec): Promise<GitExecuteResult> {
    const { command, args, cwd } = spec
    const child = spawn(command, args, {
      cwd: cwd ?? process.cwd(),
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'] as StdioOptions,
    }) as ChildProcess

    const { promise, resolve } = Promise.withResolvers<GitExecuteResult>()

    const stdoutParts: Buffer[] = []
    const stderrParts: Buffer[] = []
    let stdoutLen = 0
    let stderrLen = 0

    child.stdout?.on('data', (chunk: Buffer) => {
      stdoutLen += chunk.length
      if (stdoutLen < OUTPUT_LIMIT) stdoutParts.push(chunk)
    })

    child.stderr?.on('data', (chunk: Buffer) => {
      stderrLen += chunk.length
      if (stderrLen < OUTPUT_LIMIT) stderrParts.push(chunk)
    })

    child.on('close', (code) => {
      resolve({
        stdout: Buffer.concat(stdoutParts).toString('utf8'),
        stderr: Buffer.concat(stderrParts).toString('utf8'),
        code,
      })
    })

    child.on('error', (err) => {
      resolve({ stdout: '', stderr: err.message, code: null })
    })

    return promise
  }

  async streamExecute(
    spec: GitCommandSpec,
    onStdout: (chunk: string) => void,
    onStderr: (chunk: string) => void,
  ): Promise<GitExecuteResult> {
    const { command, args, cwd } = spec
    const child = spawn(command, args, {
      cwd: cwd ?? process.cwd(),
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'] as StdioOptions,
    }) as ChildProcess
    const { promise, resolve } = Promise.withResolvers<GitExecuteResult>()

    const stdoutParts: Buffer[] = []
    const stderrParts: Buffer[] = []
    let stdoutLen = 0
    let stderrLen = 0

    child.stdout?.on('data', (chunk: Buffer) => {
      stdoutLen += chunk.length
      onStdout(chunk.toString('utf8'))
      if (stdoutLen < OUTPUT_LIMIT) stdoutParts.push(chunk)
    })

    child.stderr?.on('data', (chunk: Buffer) => {
      stderrLen += chunk.length
      onStderr(chunk.toString('utf8'))
      if (stderrLen < OUTPUT_LIMIT) stderrParts.push(chunk)
    })

    child.on('close', (code) => {
      resolve({
        stdout: Buffer.concat(stdoutParts).toString('utf8'),
        stderr: Buffer.concat(stderrParts).toString('utf8'),
        code,
      })
    })

    child.on('error', (err) => {
      resolve({ stdout: '', stderr: err.message, code: null })
    })

    return promise
  }

  close(): void {}
}

/* ─── WslGitBackend ─────────────────────────────────────────────────── */

export class WslGitBackend extends GitBackend {
  public label = 'wsl'
  public gitBinary = 'git'

  private readonly distro: string
  private readonly process: ChildProcess
  private readonly pending = new Map<string, { resolve: (r: GitExecuteResult) => void; reject: (e: Error) => void }>()
  private readonly pendingStream = new Map<string, { onStdout: (chunk: string) => void; onStderr: (chunk: string) => void; resolve: (r: GitExecuteResult) => void; reject: (e: Error) => void }>()
  private closed = false

  constructor(distro: string) {
    super()
    this.distro = distro

    this.process = spawn('wsl.exe', ['-d', distro, '-e', 'bash', '--norc', '--noprofile'], {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    })

    const buf: Buffer[] = []

    this.process.stdout?.on('data', (chunk: Buffer) => buf.push(chunk))
    this.process.stderr?.on('data', (chunk: Buffer) => buf.push(chunk))

    this.process.stdout?.on('readable', () => {
      let chunk: string | null
      while ((chunk = this.process.stdout?.read()?.toString('utf8') ?? null) !== null) {
        this.dispatch(chunk)
      }
    })

    this.process.stderr?.on('readable', () => {
      let chunk: string | null
      while ((chunk = this.process.stderr?.read()?.toString('utf8') ?? null) !== null) {
        this.dispatch(chunk)
      }
    })

    this.process.on('close', () => {
      for (const [, { reject }] of this.pending) reject(new Error('WSL shell closed'))
      for (const [, { reject }] of this.pendingStream) reject(new Error('WSL shell closed'))
      this.pending.clear()
      this.pendingStream.clear()
    })
  }

  /**
   * Convert a Windows path to a WSL Linux path.
   */
  toLinuxPath(p: string): string {
    // Already a linux path
    if (p.startsWith('/')) return p

    // \\wsl.localhost\<distro>\path
    const wslMatch = p.match(/^\\\\wsl\.localhost\\[^\\]+\\(.+)$/)
    if (wslMatch) return '/' + wslMatch[1].replace(/\\/g, '/')

    // Drive letter: C:\path -> /mnt/c/path
    const driveMatch = p.match(/^([A-Za-z]):\\(.+)$/)
    if (driveMatch) {
      const drive = driveMatch[1].toLowerCase()
      const rest = driveMatch[2].replace(/\\/g, '/')
      return `/mnt/${drive}/${rest}`
    }

    return p.replace(/\\/g, '/')
  }

  private dispatch(data: string): void {
    // Token format: TOKEN\tEXIT_CODE\tSTDOUT_LINE
    // Uses \x1F (Unit Separator) as field delimiter
    const lines = data.split('\n')
    for (const line of lines) {
      const fields = line.split('\x1f')
      if (fields.length < 3) continue

      const token = fields[0]
      const exitCode = parseInt(fields[1], 10)
      const stdout = fields.slice(2).join('\x1f')

      // Check pending simple requests
      const entry = this.pending.get(token)
      if (entry) {
        this.pending.delete(token)
        entry.resolve({ stdout, stderr: '', code: isNaN(exitCode) ? null : exitCode })
        continue
      }

      // Check pending stream requests
      const streamEntry = this.pendingStream.get(token)
      if (streamEntry) {
        streamEntry.onStdout(stdout)
        this.pendingStream.delete(token)
        streamEntry.resolve({ stdout, stderr: '', code: isNaN(exitCode) ? null : exitCode })
      }
    }
  }

  async execute(spec: GitCommandSpec): Promise<GitExecuteResult> {
    if (this.closed) throw new Error('Backend closed')

    const token = randomUUID()
    const args = spec.args.map((a) => `"${a.replace(/"/g, '\\"')}"`).join(' ')
    const cwd = spec.cwd ? this.toLinuxPath(spec.cwd) : undefined
    const cdPrefix = cwd ? `cd "${cwd}" && ` : ''

    const cmd = `${cdPrefix}${spec.command} ${args} 2>&1; echo "${token}\x1f$?"`

    // Flush any leftover buffered output
    const leftover = this.process.stdout?.read()?.toString('utf8')
    if (leftover) this.dispatch(leftover)

    const { promise, resolve, reject } = Promise.withResolvers<GitExecuteResult>()
    this.pending.set(token, { resolve, reject })

    try {
      this.process.stdin?.write(cmd + '\n')
    } catch (err) {
      this.pending.delete(token)
      reject(err as Error)
    }

    return promise
  }

  async streamExecute(
    spec: GitCommandSpec,
    onStdout: (chunk: string) => void,
    onStderr: (chunk: string) => void,
  ): Promise<GitExecuteResult> {
    if (this.closed) throw new Error('Backend closed')

    const token = randomUUID()
    const args = spec.args.map((a) => `"${a.replace(/"/g, '\\"')}"`).join(' ')
    const cwd = spec.cwd ? this.toLinuxPath(spec.cwd) : undefined
    const cdPrefix = cwd ? `cd "${cwd}" && ` : ''

    const cmd = `${cdPrefix}${spec.command} ${args} 2>&1; echo "${token}\x1f$?"`

    const { promise, resolve, reject } = Promise.withResolvers<GitExecuteResult>()
    this.pendingStream.set(token, { onStdout, onStderr, resolve, reject })

    try {
      this.process.stdin?.write(cmd + '\n')
    } catch (err) {
      this.pendingStream.delete(token)
      reject(err as Error)
    }

    return promise
  }

  close(): void {
    this.closed = true
    try {
      this.process.stdin?.end()
      this.process.kill()
    } catch {
      // Process already dead
    }
  }
}

/* ─── SshGitBackend ─────────────────────────────────────────────────── */

export class SshGitBackend extends GitBackend {
  public label = 'ssh'
  public gitBinary = 'git'

  private readonly sshTarget: string
  private readonly repoPath: string

  constructor(target: string, repoPath: string) {
    super()
    this.sshTarget = target
    this.repoPath = repoPath
  }

  async execute(spec: GitCommandSpec): Promise<GitExecuteResult> {
    const args = spec.args.map((a) => `"${a.replace(/"/g, '\\"')}"`).join(' ')
    const remoteCmd = `cd "${this.repoPath}" && ${spec.command} ${args}`

    const child = spawn('ssh', [this.sshTarget, remoteCmd], {
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'] as StdioOptions,
    }) as ChildProcess

    const { promise, resolve } = Promise.withResolvers<GitExecuteResult>()

    const stdoutParts: Buffer[] = []
    const stderrParts: Buffer[] = []

    child.stdout?.on('data', (chunk: Buffer) => stdoutParts.push(chunk))
    child.stderr?.on('data', (chunk: Buffer) => stderrParts.push(chunk))

    child.on('close', (code) => {
      resolve({
        stdout: Buffer.concat(stdoutParts).toString('utf8'),
        stderr: Buffer.concat(stderrParts).toString('utf8'),
        code,
      })
    })

    child.on('error', (err) => {
      resolve({ stdout: '', stderr: err.message, code: null })
    })

    return promise
  }

  async streamExecute(
    spec: GitCommandSpec,
    onStdout: (chunk: string) => void,
    onStderr: (chunk: string) => void,
  ): Promise<GitExecuteResult> {
    const args = spec.args.map((a) => `"${a.replace(/"/g, '\\"')}"`).join(' ')
    const remoteCmd = `cd "${this.repoPath}" && ${spec.command} ${args}`

    const child = spawn('ssh', [this.sshTarget, remoteCmd], {
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'] as StdioOptions,
    }) as ChildProcess

    const { promise, resolve } = Promise.withResolvers<GitExecuteResult>()

    const stdoutParts: Buffer[] = []
    const stderrParts: Buffer[] = []

    child.stdout?.on('data', (chunk: Buffer) => {
      onStdout(chunk.toString('utf8'))
      stdoutParts.push(chunk)
    })

    child.stderr?.on('data', (chunk: Buffer) => {
      onStderr(chunk.toString('utf8'))
      stderrParts.push(chunk)
    })

    child.on('close', (code) => {
      resolve({
        stdout: Buffer.concat(stdoutParts).toString('utf8'),
        stderr: Buffer.concat(stderrParts).toString('utf8'),
        code,
      })
    })

    child.on('error', (err) => {
      resolve({ stdout: '', stderr: err.message, code: null })
    })

    return promise
  }

  close(): void {}
}

/* ─── Backend pool ──────────────────────────────────────────────────── */

const wslBackends = new Map<string, WslGitBackend>()

export function getWslBackend(distro: string): WslGitBackend {
  let backend = wslBackends.get(distro)
  if (!backend) {
    backend = new WslGitBackend(distro)
    wslBackends.set(distro, backend)
  }
  return backend
}

/* ─── Detect & create ───────────────────────────────────────────────── */

export function detectBackend(repoPath: string): GitBackend {
  if (isWslPath(repoPath)) {
    const distro = getDistroFromWslPath(repoPath)
    return distro ? getWslBackend(distro) : new NativeGitBackend()
  }

  const sshParsed = parseSshPath(repoPath)
  if (sshParsed) {
    return new SshGitBackend(`${sshParsed.user}@${sshParsed.host}`, sshParsed.repoPath)
  }

  return new NativeGitBackend()
}

/** Close all WSL backend shells. Call on shutdown. */
export function closeAllBackends(): void {
  for (const backend of wslBackends.values()) {
    backend.close()
  }
  wslBackends.clear()
}