import path from 'node:path'

export function normalizeRepoPath(raw: string): string {
  return path.resolve(raw.trim())
}

export function isWslPath(p: string): boolean {
  const wslRe = /^\\\\wsl\.localhost/
  const mntRe = /^\/mnt\//
  return wslRe.test(p) || mntRe.test(p)
}

export function getDistroFromWslPath(p: string): string | null {
  const re = /^\\\\wsl\.localhost\\([^\\]+)\\/
  const m = p.match(re)
  return m ? m[1] : null
}

export function parseSshPath(raw: string): { user: string; host: string; repoPath: string } | null {
  const re = /^(.+?)@(.+?):(.+)$/
  const m = raw.match(re)
  if (!m) return null
  return { user: m[1], host: m[2], repoPath: m[3] }
}