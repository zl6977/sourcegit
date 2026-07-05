import { useEffect, useState, useCallback } from 'react'
import { useRepo } from '../hooks/useRepo'
import { useGitOperation } from '../hooks/useGitOperation'
import type { StashEntry } from '@sourcegit/shared'
import { useToast } from '../components/Toast'

export default function Stashes() {
  const { repoPath } = useRepo()
  const { execute } = useGitOperation()
  const { addToast } = useToast()

  const [stashes, setStashes] = useState<StashEntry[]>([])

  const loadStashes = useCallback(async () => {
    if (!repoPath) return
    try {
      const result = await execute<{ stashes: StashEntry[] }>({ name: 'stash_list', input: { path: repoPath } })
      setStashes(result.stashes)
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to load stashes', 'error')
    }
  }, [repoPath, execute, addToast])

  useEffect(() => {
    loadStashes()
  }, [loadStashes])

  const handleApply = useCallback(async (index: number) => {
    try {
      await execute({ name: 'stash_apply', input: { path: repoPath, index } })
      addToast(`Applied stash ${index}`, 'success')
      await loadStashes()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to apply stash', 'error')
    }
  }, [repoPath, execute, addToast, loadStashes])

  const handlePop = useCallback(async (index: number) => {
    try {
      await execute({ name: 'stash_pop', input: { path: repoPath, index } })
      addToast(`Popped stash ${index}`, 'success')
      await loadStashes()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to pop stash', 'error')
    }
  }, [repoPath, execute, addToast, loadStashes])

  const handleDrop = useCallback(async (index: number) => {
    if (!confirm('Drop this stash?')) return
    try {
      await execute({ name: 'stash_drop', input: { path: repoPath, index } })
      addToast(`Dropped stash ${index}`, 'success')
      await loadStashes()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to drop stash', 'error')
    }
  }, [repoPath, execute, addToast, loadStashes])

  if (!repoPath) {
    return <div className="text-slate-400 p-4">No repository selected</div>
  }

  if (stashes.length === 0) {
    return <div className="text-slate-500 text-sm p-4">No stashes</div>
  }

  return (
    <div className="flex flex-col h-full overflow-auto">
      {stashes.map(stash => (
        <div key={stash.index} className="border-b border-slate-700 hover:bg-slate-800">
          <div className="flex items-center gap-3 px-4 py-3">
            <span className="text-xs text-slate-500 w-8 text-right">{stash.index}</span>
            <div className="flex-1 min-w-0">
              <div className="text-sm text-white truncate">{stash.message}</div>
              <div className="text-xs text-slate-500">
                {stash.sha.slice(0, 7)} {stash.isIndex ? '(with index)' : ''}
              </div>
            </div>
            <div className="flex gap-1 shrink-0">
              <button onClick={() => handleApply(stash.index)} className="px-2 py-1 bg-blue-600 text-white text-xs rounded hover:bg-blue-700">Apply</button>
              <button onClick={() => handlePop(stash.index)} className="px-2 py-1 bg-green-600 text-white text-xs rounded hover:bg-green-700">Pop</button>
              <button onClick={() => handleDrop(stash.index)} className="px-2 py-1 bg-red-600 text-white text-xs rounded hover:bg-red-700">Drop</button>
            </div>
          </div>
        </div>
      ))}
    </div>
  )
}