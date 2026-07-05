import { useEffect, useState, useCallback } from 'react'
import { useRepo } from '../hooks/useRepo'
import { useGitOperation } from '../hooks/useGitOperation'
import type { Branch } from '@sourcegit/shared'
import { BranchTree } from '../components/BranchTree'
import { useToast } from '../components/Toast'

export default function Branches() {
  const { repoPath } = useRepo()
  const { execute } = useGitOperation()
  const { addToast } = useToast()

  const [branches, setBranches] = useState<Branch[]>([])
  const [showCreateDialog, setShowCreateDialog] = useState(false)
  const [createName, setCreateName] = useState('')
  const [renameTarget, setRenameTarget] = useState<string | null>(null)
  const [renameValue, setRenameValue] = useState('')

  const loadBranches = useCallback(async () => {
    if (!repoPath) return
    try {
      const result = await execute<{ branches: Branch[] }>({ name: 'branch_list', input: { path: repoPath } })
      setBranches(result.branches)
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to load branches', 'error')
    }
  }, [repoPath, execute, addToast])

  useEffect(() => {
    loadBranches()
  }, [loadBranches])

  const handleCheckout = useCallback(async (name: string) => {
    try {
      await execute({ name: 'checkout', input: { path: repoPath, branch: name } })
      addToast(`Checked out ${name}`, 'success')
      await loadBranches()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Checkout failed', 'error')
    }
  }, [repoPath, execute, addToast, loadBranches])

  const handleCreate = useCallback(async () => {
    if (!createName.trim()) return
    try {
      await execute({ name: 'branch_create', input: { path: repoPath, name: createName.trim() } })
      addToast(`Created branch ${createName}`, 'success')
      setCreateName('')
      setShowCreateDialog(false)
      await loadBranches()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to create branch', 'error')
    }
  }, [createName, repoPath, execute, addToast, loadBranches])

  const handleDelete = useCallback(async (name: string) => {
    if (!confirm(`Delete branch '${name}'?`)) return
    try {
      await execute({ name: 'branch_delete', input: { path: repoPath, name } })
      addToast(`Deleted branch ${name}`, 'success')
      await loadBranches()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to delete branch', 'error')
    }
  }, [repoPath, execute, addToast, loadBranches])

  const handleRenameStart = useCallback((name: string) => {
    setRenameTarget(name)
    setRenameValue(name)
  }, [])

  const handleRename = useCallback(async () => {
    if (!renameTarget || !renameValue.trim()) return
    try {
      await execute({ name: 'branch_rename', input: { path: repoPath, oldName: renameTarget, newName: renameValue.trim() } })
      addToast(`Renamed to ${renameValue}`, 'success')
      setRenameTarget(null)
      setRenameValue('')
      await loadBranches()
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to rename branch', 'error')
    }
  }, [renameTarget, renameValue, repoPath, execute, addToast, loadBranches])

  if (!repoPath) {
    return <div className="text-slate-400 p-4">No repository selected</div>
  }

  return (
    <div className="flex flex-col h-full">
      {showCreateDialog && (
        <div className="px-4 py-2 bg-slate-800 border-b border-slate-700 flex items-center gap-2">
          <input
            type="text"
            value={createName}
            onChange={e => setCreateName(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && handleCreate()}
            placeholder="Branch name..."
            className="bg-slate-700 text-sm text-slate-200 px-3 py-1 rounded border border-slate-600 focus:outline-none focus:border-blue-500"
            autoFocus
          />
          <button onClick={handleCreate} className="px-3 py-1 bg-blue-600 text-white text-xs rounded hover:bg-blue-700">Create</button>
          <button onClick={() => setShowCreateDialog(false)} className="px-3 py-1 bg-slate-600 text-slate-200 text-xs rounded hover:bg-slate-500">Cancel</button>
        </div>
      )}
      <BranchTree
        branches={branches}
        onCheckout={handleCheckout}
        onCreate={() => setShowCreateDialog(true)}
        onDelete={handleDelete}
        onRename={handleRenameStart}
      />
      {renameTarget && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
          <div className="bg-slate-800 p-6 rounded-lg w-80">
            <h3 className="text-white text-sm mb-3">Rename Branch</h3>
            <input
              type="text"
              value={renameValue}
              onChange={e => setRenameValue(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && handleRename()}
              className="w-full bg-slate-700 text-sm text-slate-200 px-3 py-2 rounded border border-slate-600 focus:outline-none focus:border-blue-500 mb-3"
              autoFocus
            />
            <div className="flex gap-2">
              <button onClick={handleRename} className="flex-1 px-3 py-1.5 bg-blue-600 text-white text-xs rounded hover:bg-blue-700">Rename</button>
              <button onClick={() => setRenameTarget(null)} className="flex-1 px-3 py-1.5 bg-slate-600 text-slate-200 text-xs rounded hover:bg-slate-500">Cancel</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}