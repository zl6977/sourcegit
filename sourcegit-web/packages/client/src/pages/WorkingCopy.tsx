import { useEffect, useState, useCallback } from 'react'
import { useRepo } from '../hooks/useRepo'
import { useGitOperation } from '../hooks/useGitOperation'
import type { GitStatus, FileChange, DiffLine } from '@sourcegit/shared'
import { FileTree } from '../components/FileTree'
import { DiffView } from '../components/DiffView'
import { CommitPanel } from '../components/CommitPanel'
import { StatusBar } from '../components/StatusBar'
import { useToast } from '../components/Toast'

export default function WorkingCopy() {
  const { repoName, repoPath } = useRepo()
  const { execute } = useGitOperation()
  const { addToast } = useToast()

  const [status, setStatus] = useState<GitStatus | null>(null)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [selectedPanel, setSelectedPanel] = useState<'staged' | 'unstaged' | 'untracked'>('unstaged')
  const [diffLines, setDiffLines] = useState<DiffLine[]>([])

  const refresh = useCallback(async () => {
    if (!repoPath) return
    try {
      const result = await execute<GitStatus>({ name: 'status', input: { path: repoPath } })
      setStatus(result)
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to refresh', 'error')
    }
  }, [repoPath, execute, addToast])

  useEffect(() => {
    refresh()
  }, [refresh])

  const handleStage = useCallback(async (paths: string[]) => {
    if (!repoPath) return
    try {
      await execute({ name: 'add', input: { path: repoPath, paths } })
      await refresh()
      addToast('Staged', 'success')
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to stage', 'error')
    }
  }, [repoPath, execute, refresh, addToast])

  const handleUnstage = useCallback(async (paths: string[]) => {
    if (!repoPath) return
    try {
      await execute({ name: 'reset', input: { path: repoPath, paths, soft: false } })
      await refresh()
      addToast('Unstaged', 'success')
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to unstage', 'error')
    }
  }, [repoPath, execute, refresh, addToast])

  const handleDiscard = useCallback(async (paths: string[]) => {
    if (!repoPath) return
    try {
      await execute({ name: 'checkout_file', input: { path: repoPath, paths } })
      await refresh()
      addToast('Discarded', 'success')
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Failed to discard', 'error')
    }
  }, [repoPath, execute, refresh, addToast])

  const handleReset = useCallback(async () => {
    if (!repoPath) return
    try {
      await execute({ name: 'reset', input: { path: repoPath, revision: 'HEAD', hard: true } })
      await refresh()
      addToast('Reset HEAD', 'success')
    } catch (err) {
      addToast(err instanceof Error ? err.message : 'Reset failed', 'error')
    }
  }, [repoPath, execute, refresh, addToast])

  const loadDiff = useCallback(async (filePath: string) => {
    if (!repoPath) return
    try {
      const diff = await execute<{ lines: DiffLine[] }>({ name: 'diff', input: { path: repoPath, file: filePath } })
      setDiffLines(diff.lines)
    } catch {
      setDiffLines([])
    }
  }, [repoPath, execute])

  const handleFileSelect = useCallback((filePath: string, panel: 'staged' | 'unstaged' | 'untracked') => {
    setSelectedFile(filePath)
    setSelectedPanel(panel)
    loadDiff(filePath)
  }, [loadDiff])

  const handleStageFile = useCallback((path: string) => {
    handleStage([path])
    if (selectedFile === path) {
      setSelectedFile(null)
      setDiffLines([])
    }
  }, [handleStage, selectedFile])

  const handleUnstageFile = useCallback((path: string) => {
    handleUnstage([path])
    if (selectedFile === path) {
      setSelectedFile(null)
      setDiffLines([])
    }
  }, [handleUnstage, selectedFile])

  const handleDiscardFile = useCallback((path: string) => {
    handleDiscard([path])
    if (selectedFile === path) {
      setSelectedFile(null)
      setDiffLines([])
    }
  }, [handleDiscard, selectedFile])

  if (!repoPath) {
    return <div className="text-slate-400 p-4">No repository selected</div>
  }

  const currentPanel = selectedPanel === 'staged' ? status?.staged :
    selectedPanel === 'unstaged' ? status?.unstaged :
      status?.untracked

  return (
    <div className="flex flex-col h-full">
      {/* Toolbar */}
      <div className="flex items-center gap-2 px-4 py-2 border-b border-slate-700">
        <button onClick={refresh} className="px-3 py-1 bg-slate-700 text-slate-200 text-xs rounded hover:bg-slate-600">
          Refresh
        </button>
        <button onClick={handleReset} className="px-3 py-1 bg-red-700 text-white text-xs rounded hover:bg-red-600">
          Reset HEAD
        </button>
      </div>

      {/* Content */}
      <div className="flex flex-1 overflow-hidden">
        {/* Panels */}
        <div className="flex flex-col w-80 border-r border-slate-700">
          {/* Panel tabs */}
          <div className="flex border-b border-slate-700">
            {(['unstaged', 'untracked', 'staged'] as const).map(panel => (
              <button
                key={panel}
                onClick={() => { setSelectedPanel(panel); setSelectedFile(null); setDiffLines([]) }}
                className={`flex-1 px-2 py-2 text-xs capitalize
                  ${selectedPanel === panel ? 'bg-slate-700 text-white border-b-2 border-blue-500' : 'text-slate-400 hover:text-slate-300'}
                `}
              >
                {panel}
              </button>
            ))}
          </div>

          {/* File list */}
          <div className="flex-1 overflow-auto">
            <FileTree
              files={(currentPanel || []) as FileChange[]}
              selected={selectedPanel === selectedPanel && selectedFile ? selectedFile : null}
              onSelect={(path) => handleFileSelect(path, selectedPanel)}
              emptyText="No files"
            />
          </div>

          {/* Action buttons */}
          <div className="flex gap-1 p-2 border-t border-slate-700">
            {selectedPanel !== 'staged' && (
              <button
                onClick={() => selectedFile && handleStageFile(selectedFile)}
                disabled={!selectedFile}
                className="flex-1 px-2 py-1 bg-blue-600 text-white text-xs rounded hover:bg-blue-700 disabled:opacity-50"
              >
                Stage
              </button>
            )}
            {selectedPanel === 'staged' && (
              <button
                onClick={() => selectedFile && handleUnstageFile(selectedFile)}
                disabled={!selectedFile}
                className="flex-1 px-2 py-1 bg-yellow-600 text-white text-xs rounded hover:bg-yellow-700 disabled:opacity-50"
              >
                Unstage
              </button>
            )}
            {selectedPanel !== 'staged' && selectedPanel !== 'untracked' && (
              <button
                onClick={() => selectedFile && handleDiscardFile(selectedFile)}
                disabled={!selectedFile}
                className="flex-1 px-2 py-1 bg-red-600 text-white text-xs rounded hover:bg-red-700 disabled:opacity-50"
              >
                Discard
              </button>
            )}
          </div>
        </div>

        {/* Diff panel */}
        <div className="flex-1 overflow-hidden">
          {selectedFile ? (
            <div className="flex flex-col h-full">
              <div className="px-4 py-2 bg-slate-800 border-b border-slate-700 text-sm text-slate-300 truncate">
                {selectedFile}
              </div>
              <div className="flex-1 overflow-auto bg-slate-900">
                <DiffView diff={diffLines} />
              </div>
            </div>
          ) : (
            <div className="flex items-center justify-center h-full text-slate-500 text-sm">
              Select a file to view diff
            </div>
          )}
        </div>
      </div>

      {/* Commit panel */}
      <CommitPanel repoPath={repoPath} onCommitted={refresh} />

      {/* Status bar */}
      <StatusBar
        branch={status?.currentBranch || ''}
        aheadBy={status?.aheadBy}
        behindBy={status?.behindBy}
        stagedCount={status?.staged.length}
        unstagedCount={(status?.unstaged.length || 0) + (status?.untracked.length || 0)}
      />
    </div>
  )
}