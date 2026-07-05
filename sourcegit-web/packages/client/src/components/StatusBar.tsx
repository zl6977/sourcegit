interface StatusBarProps {
  branch: string
  aheadBy?: number
  behindBy?: number
  stagedCount?: number
  unstagedCount?: number
}

export function StatusBar({ branch, aheadBy = 0, behindBy = 0, stagedCount = 0, unstagedCount = 0 }: StatusBarProps) {
  return (
    <div className="flex items-center gap-4 px-4 py-1 bg-slate-800 border-t border-slate-700 text-xs text-slate-400">
      <span className="flex items-center gap-1">
        <span className="text-blue-400">⟳</span>
        {branch}
      </span>
      {aheadBy > 0 && <span className="text-green-400">↑{aheadBy}</span>}
      {behindBy > 0 && <span className="text-yellow-400">↓{behindBy}</span>}
      <span className="text-yellow-400">{stagedCount} staged</span>
      <span className="text-red-400">{unstagedCount} unstaged</span>
      <div className="flex-1" />
      <span>SourceGit Web</span>
    </div>
  )
}