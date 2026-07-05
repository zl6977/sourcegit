import type {
  FileChange,
  FileStatus,
  Commit,
  Diff,
  Branch,
  Remote,
  Tag,
  StashEntry,
  DiffLine,
} from '@sourcegit/shared'

/* ─── Status parsing ────────────────────────────────────────────────── */

export function parseStatusLine(line: string): FileChange | null {
  if (line.length < 4) return null

  const x = line[0]
  const y = line[1]
  const rest = line.substring(3)

  const status = mapStatusChar(x + y)
  if (!status) return null

  const operation = line[2] || ' '

  let path = rest
  let score: number | undefined

  if (x === 'R' || y === 'R') {
    const arrowIdx = rest.indexOf(' -> ')
    if (arrowIdx >= 0) {
      const before = rest.substring(0, arrowIdx)
      path = rest.substring(arrowIdx + 4)
      const scoreMatch = before.match(/(\d+)$/)
      score = scoreMatch ? parseInt(scoreMatch[1], 10) : 100
    }
  }

  return {
    path,
    status,
    operation: operation as FileChange['operation'],
    newFile: status.startsWith('A') || status === 'R' || status === 'C',
    score,
  }
}

function mapStatusChar(xy: string): FileStatus {
  switch (xy) {
    case 'A ': case 'AA': return 'A'
    case 'M ': case 'MM': return 'M'
    case 'D ': case 'DD': return 'DD'
    case 'R ': case 'RR': return 'R'
    case 'C ': case 'CC': return 'C'
    case 'U ': case 'UU': return 'UU'
    case '? ': case '??': return '??'
    case 'AM': return 'AM'
    case 'AU': return 'AU'
    case 'AD': return 'AD'
    case 'UD': return 'UD'
    default: return 'M'
  }
}

export function parseStatusOutput(output: string): {
  staged: FileChange[]
  unstaged: FileChange[]
  untracked: FileChange[]
} {
  const staged: FileChange[] = []
  const unstaged: FileChange[] = []
  const untracked: FileChange[] = []

  const lines = output.split('\0').filter(Boolean)

  for (const line of lines) {
    const fc = parseStatusLine(line)
    if (!fc) continue

    if (fc.status === '??') {
      untracked.push(fc)
    } else if (line[0] === ' ') {
      unstaged.push(fc)
    } else {
      staged.push(fc)
    }
  }

  return { staged, unstaged, untracked }
}

export function parseFileStats(output: string): FileChange[] {
  const lines = output.split('\0').filter(Boolean)
  return lines
    .map((line) => parseStatusLine(line))
    .filter((s): s is FileChange => s !== null)
}

/* ─── Commit parsing ────────────────────────────────────────────────── */

export function parseCommits(output: string): Commit[] {
  const commits: Commit[] = []
  const lines = output.split('\n')
  let buffer = ''

  for (const line of lines) {
    const firstSepIdx = line.indexOf('\x1f')
    if (firstSepIdx > 0) {
      const possibleHash = line.substring(0, firstSepIdx)
      if (/^[0-9a-f]{40}$/.test(possibleHash)) {
        if (buffer.trim()) {
          const parsed = parseSingleCommit(buffer.trim())
          if (parsed) commits.push(parsed)
        }
        buffer = line + '\n'
        continue
      }
    }
    buffer += line + '\n'
  }

  if (buffer.trim()) {
    const parsed = parseSingleCommit(buffer.trim())
    if (parsed) commits.push(parsed)
  }

  return commits
}

function parseSingleCommit(buffer: string): Commit | null {
  const lines = buffer.split('\n')

  let dataLineIdx = -1
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    const sepIdx = line.indexOf('\x1f')
    if (sepIdx > 0) {
      const possibleHash = line.substring(0, sepIdx)
      if (/^[0-9a-f]{40}$/.test(possibleHash)) {
        dataLineIdx = i
        break
      }
    }
  }

  if (dataLineIdx < 0) return null

  const dataLine = lines[dataLineIdx]
  const parts = dataLine.split('\x1f')

  if (parts.length < 12) return null

  const sha = parts[0]
  const shortSha = parts[1]
  const authorName = parts[2]
  const authorEmail = parts[3]
  const authorDateStr = parts[4]
  const committerName = parts[5]
  const committerEmail = parts[6]
  const committerDateStr = parts[7]
  const parentsStr = parts[8]
  const treeSha = parts[9]

  const parents: string[] = parentsStr.trim()
    ? parentsStr.trim().split(/\s+/)
    : []

  let bodyStart = 0
  for (let i = 0; i < 11 && bodyStart < dataLine.length; i++) {
    bodyStart = dataLine.indexOf('\x1f', bodyStart) + 1
  }
  let message = dataLine.substring(bodyStart)

  for (let i = dataLineIdx + 1; i < lines.length; i++) {
    message += '\n' + lines[i]
  }

  const messageLines = message.trim().split('\n')
  const shortMessage = messageLines[0]

  return {
    sha,
    shortSha,
    message: messageLines.join('\n'),
    shortMessage,
    author: { name: authorName, email: authorEmail, date: authorDateStr },
    committer: { name: committerName, email: committerEmail, date: committerDateStr },
    parents,
    tags: [],
    treeSha,
    commitCount: 0,
  }
}

/* ─── Diff parsing ──────────────────────────────────────────────────── */

export function parseDiffOutput(output: string): Diff[] {
  const diffs: Diff[] = []
  let currentDiff: Diff | null = null

  for (const line of output.split('\n')) {
    const headerMatch = line.match(/^diff --git a\/(.+) b\/(.+)$/)
    if (headerMatch) {
      if (currentDiff) diffs.push(currentDiff)
      currentDiff = {
        oldPath: headerMatch[1],
        newPath: headerMatch[2],
        isBinary: false,
        lines: [],
      }
      continue
    }

    if (!currentDiff) continue

    const hunkMatch = line.match(
      /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/,
    )
    if (hunkMatch) {
      currentDiff.lines.push({
        type: '@' as DiffLine['type'],
        content: line,
        lineBefore: parseInt(hunkMatch[1], 10),
        lineAfter: parseInt(hunkMatch[2], 10),
      })
      continue
    }

    if (line.startsWith('+') && !line.startsWith('+++')) {
      currentDiff.lines.push({ type: '+', content: line })
    } else if (line.startsWith('-') && !line.startsWith('---')) {
      currentDiff.lines.push({ type: '-', content: line })
    } else if (line.startsWith(' ')) {
      currentDiff.lines.push({ type: ' ', content: line })
    }
  }

  if (currentDiff) diffs.push(currentDiff)
  return diffs
}

/* ─── Branch parsing ────────────────────────────────────────────────── */

export function parseBranches(
  output: string,
  currentBranch: string,
): Branch[] {
  const branches: Branch[] = []
  const entries = output.split('\0').filter(Boolean)

  for (const entry of entries) {
    const parts = entry.split('\x1f')
    if (parts.length < 2) continue

    const name = parts[0]
    const sha = parts[1] || ''

    const isLocal = !name.startsWith('remotes/')
    const shortName = isLocal ? name : name.replace('remotes/', '')
    const upstream = parts[2] || undefined

    branches.push({
      name,
      shortName,
      sha,
      upstream,
      isCurrent: name === currentBranch,
      isLocal,
    })
  }

  return branches
}

/* ─── Remote parsing ────────────────────────────────────────────────── */

export function parseRemotes(output: string): Remote[] {
  const remotes: Remote[] = []
  const seen = new Set<string>()

  for (const entry of output.split('\0').filter(Boolean)) {
    const parts = entry.split('\n')
    if (parts.length < 2) continue

    const nameUrl = parts[0]
    if (!nameUrl || seen.has(nameUrl)) continue
    seen.add(nameUrl)

    const tabIdx = nameUrl.indexOf('\t')
    if (tabIdx < 0) continue

    const name = nameUrl.substring(0, tabIdx)
    const url = nameUrl.substring(tabIdx + 1)

    remotes.push({ name, url })
  }

  return remotes
}

/* ─── Tag parsing ───────────────────────────────────────────────────── */

export function parseTags(output: string): Tag[] {
  const tags: Tag[] = []

  for (const entry of output.split('\0').filter(Boolean)) {
    const lines = entry.split('\n')
    if (lines.length < 1) continue

    const name = lines[0].trim()
    const message = lines.slice(1).join('\n').trim() || undefined

    tags.push({ name, sha: '', message })
  }

  return tags
}

/* ─── Stash parsing ─────────────────────────────────────────────────── */

export function parseStashes(output: string): StashEntry[] {
  const stashes: StashEntry[] = []
  let index = 0

  for (const entry of output.split('\n').filter(Boolean)) {
    const parts = entry.split('\x1f')
    if (parts.length < 2) continue

    const sha = parts[0] || ''
    const message = parts[1] || ''

    stashes.push({
      index: index++,
      sha,
      message,
      isIndex: false,
    })
  }

  return stashes
}

/* ─── Tree parsing ──────────────────────────────────────────────────── */

export function parseTreeOutput(output: string): string[] {
  return output.split('\n').filter(Boolean)
}