import { useState, useCallback } from 'react'
import { useGitOperation } from '../hooks/useGitOperation'
import { useToast } from './Toast'

interface CommitPanelProps {
  repoPath: string
  onCommitted: () => void
  isAmend?: boolean
}

export function CommitPanel({ repoPath, onCommitted, isAmend = false }: CommitPanelProps) {
  const [message, setMessage] = useState('')
  const [isCommitting, setIsCommitting] = useState(false)
  const { execute } = useGitOperation()
  const { addToast } = useToast()

  const handleCommit = useCallback(async () => {
    if (!message.trim()) {
      addToast('Commit message is required', 'error')
      return
    }

    setIsCommitting(true)
    try {
      const opName = isAmend ? 'commit_amend' : 'commit'
      await execute({
        name: opName,
        input: { path: repoPath, message: message.trim() }
      })
      setMessage('')
      addToast(isAmend ? 'Commit amended successfully' : 'Committed successfully', 'success')
      onCommitted()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Commit failed', 'error')
    } finally {
      setIsCommitting(false)
    }
  }, [message, repoPath, isAmend, execute, addToast, onCommitted])

  return (
    <div className="border-t border-slate-700 p-3 flex gap-2">
      <input
        type="text"
        value={message}
        onChange={e => setMessage(e.target.value)}
        onKeyDown={e => e.key === 'Enter' && handleCommit()}
        placeholder={isAmend ? 'Amend message...' : 'Commit message...'}
        className="flex-1 bg-slate-800 text-sm text-slate-200 px-3 py-2 rounded border border-slate-600 focus:outline-none focus:border-blue-500"
      />
      <button
        onClick={handleCommit}
        disabled={!message.trim() || isCommitting}
        className="px-4 py-2 bg-green-600 text-white text-sm rounded hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
      >
        {isCommitting ? '...' : isAmend ? 'Amend' : 'Commit'}
      </button>
    </div>
  )
}