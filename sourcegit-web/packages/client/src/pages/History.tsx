import { useEffect, useState, useCallback } from 'react'
import { useRepo } from '../hooks/useRepo'
import { useGitOperation } from '../hooks/useGitOperation'
import type { Commit, GitStatus } from '@sourcegit/shared'
import { StatusBar } from '../components/StatusBar'
import { useToast } from '../components/Toast'

const GRAPH_CHAR_COLORS: Record<string, string> = {
  '*': 'text-white',
  '|': 'text-blue-400',
  '\\': 'text-green-400',
  '-': 'text-orange-400',
  '~': 'text-purple-400',
  '/': 'text-green-400'
}

interface GraphRow {
  type: 'graph' | 'commit'
  text: string
  commit?: Commit
}

export default function History() {
  const { repoName, repoPath } = useRepo()
  const { execute } = useGitOperation()
  const { addToast } = useToast()

  const [commits, setCommits] = useState<Commit[]>([])
  const [graphRows, setGraphRows] = useState<GraphRow[]>([])
  const [selectedCommit, setSelectedCommit] = useState<Commit | null>(null)
  const [status, setStatus] = useState<GitStatus | null>(null)

  const loadHistory = useCallback(async () => {
    if (!repoPath) return
    try {
      const result = await execute<{ commits: Commit[] }>({ name: 'log', input: { path: repoPath } })
      setCommits(result.commits)
      parseGraph(result.commits)

      const status = await execute<GitStatus>({ name: 'status', input: { path: repoPath } })
      setStatus(status)
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to load history', 'error')
    }
  }, [repoPath, execute, addToast])

  useEffect(() => {
    loadHistory()
  }, [loadHistory])

  function parseGraph(commits: Commit[]) {
    const rows: GraphRow[] = []
    for (const commit of commits) {
      rows.push({ type: 'commit', text: commit.shortSha + ' ' + commit.shortMessage, commit })
    }
    setGraphRows(rows)
  }

  const handleResetHard = useCallback(async () => {
    if (!selectedCommit || !repoPath) return
    try {
      await execute({ name: 'reset', input: { path: repoPath, revision: selectedCommit.sha, hard: true } })
      addToast('HEAD reset to ' + selectedCommit.shortSha, 'success')
      await loadHistory()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Reset failed', 'error')
    }
  }, [selectedCommit, repoPath, execute, addToast, loadHistory])

  const handleResetSoft = useCallback(async () => {
    if (!selectedCommit || !repoPath) return
    try {
      await execute({ name: 'reset_soft', input: { path: repoPath, revision: selectedCommit.sha } })
      addToast('Soft reset to ' + selectedCommit.shortSha, 'success')
      await loadHistory()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Reset failed', 'error')
    }
  }, [selectedCommit, repoPath, execute, addToast, loadHistory])

  const handleCheckout = useCallback(async () => {
    if (!selectedCommit || !repoPath) return
    try {
      await execute({ name: 'checkout', input: { path: repoPath, revision: selectedCommit.sha } })
      addToast('Checked out ' + selectedCommit.shortSha, 'success')
      await loadHistory()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Checkout failed', 'error')
    }
  }, [selectedCommit, repoPath, execute, addToast, loadHistory])

  const renderGraphChar = (char: string): JSX.Element => {
    const colorClass = GRAPH_CHAR_COLORS[char] || 'text-slate-400'
    return <span key={char} className={colorClass}>{char}</span>
  }

  if (!repoPath) {
    return <div className="text-slate-400 p-4">No repository selected</div>
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex flex-1 overflow-hidden">
        <div className="flex-1 overflow-auto bg-slate-900">
          <div className="font-mono text-xs p-2">
            {graphRows.map((row, i) => (
              <div
                key={i}
                onClick={() => row.commit && setSelectedCommit(row.commit)}
                className={`${row.type === 'commit' ? 'cursor-pointer hover:bg-slate-800' : ''}
                  ${selectedCommit?.sha === row.commit?.sha ? 'bg-slate-700' : ''}
                  px-2 py-0.5
                `}
              >
                {row.type === 'graph' ? (
                  [...row.text].map((char, j) => renderGraphChar(char))
                ) : (
                  <span className="text-yellow-400">{row.commit?.shortSha || ''}</span>
                )}
                {row.type === 'commit' && (
                  <span className="text-slate-300 ml-2">{row.commit?.shortMessage || ''}</span>
                )}
              </div>
            ))}
          </div>
        </div>

        {selectedCommit && (
          <div className="w-80 border-l border-slate-700 bg-slate-800 overflow-auto">
            <div className="p-4">
              <div className="text-xs text-slate-500 mb-1">{selectedCommit.sha}</div>
              <h3 className="text-sm text-white font-medium mb-2">{selectedCommit.shortMessage}</h3>
              <p className="text-xs text-slate-400 mb-3 whitespace-pre-wrap">{selectedCommit.message}</p>
              <div className="text-xs text-slate-500 mb-1">
                {selectedCommit.author.name} &lt;{selectedCommit.author.email}&gt;
              </div>
              <div className="text-xs text-slate-500 mb-4">{selectedCommit.author.date}</div>
              {selectedCommit.tags.length > 0 && (
                <div className="flex flex-wrap gap-1 mb-4">
                  {selectedCommit.tags.map(tag => (
                    <span key={tag} className="px-2 py-0.5 bg-purple-900 text-purple-300 text-xs rounded">
                      {tag}
                    </span>
                  ))}
                </div>
              )}
              <div className="flex flex-col gap-2">
                <button onClick={handleResetHard} className="px-3 py-1.5 bg-red-700 text-white text-xs rounded hover:bg-red-600">
                  Reset HEAD (Hard)
                </button>
                <button onClick={handleResetSoft} className="px-3 py-1.5 bg-yellow-700 text-white text-xs rounded hover:bg-yellow-600">
                  Soft Reset
                </button>
                <button onClick={handleCheckout} className="px-3 py-1.5 bg-blue-700 text-white text-xs rounded hover:bg-blue-600">
                  Checkout
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      <StatusBar
        branch={status?.currentBranch || ''}
        aheadBy={status?.aheadBy}
        behindBy={status?.behindBy}
      />
    </div>
  )
}