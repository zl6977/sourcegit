import type { Branch } from '@sourcegit/shared'

interface BranchTreeProps {
  branches: Branch[]
  onCheckout: (name: string) => void
  onCreate: () => void
  onDelete: (name: string) => void
  onRename: (oldName: string) => void
}

export function BranchTree({ branches, onCheckout, onCreate, onDelete, onRename }: BranchTreeProps) {
  const localBranches = branches.filter(b => b.isLocal)
  const remoteBranches = branches.filter(b => !b.isLocal)

  return (
    <div className="flex flex-col h-full">
      <div className="p-2 border-b border-slate-700 flex gap-2">
        <button
          onClick={onCreate}
          className="px-3 py-1 bg-blue-600 text-white text-xs rounded hover:bg-blue-700"
        >
          New Branch
        </button>
      </div>
      <div className="flex-1 overflow-auto">
        {localBranches.length > 0 && (
          <div className="py-1">
            <div className="px-3 py-1 text-xs text-slate-500 uppercase font-bold">Local</div>
            {localBranches.map(branch => (
              <BranchItem
                key={branch.sha}
                branch={branch}
                onCheckout={() => onCheckout(branch.name)}
                onDelete={() => onDelete(branch.name)}
                onRename={() => onRename(branch.name)}
              />
            ))}
          </div>
        )}
        {remoteBranches.length > 0 && (
          <div className="py-1">
            <div className="px-3 py-1 text-xs text-slate-500 uppercase font-bold">Remote</div>
            {remoteBranches.map(branch => (
              <BranchItem
                key={branch.sha}
                branch={branch}
                onCheckout={() => onCheckout(branch.name)}
                onDelete={() => onDelete(branch.name)}
                onRename={() => onRename(branch.name)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

interface BranchItemProps {
  branch: Branch
  onCheckout: () => void
  onDelete: () => void
  onRename: () => void
}

function BranchItem({ branch, onCheckout, onDelete, onRename }: BranchItemProps) {
  return (
    <div className="flex items-center gap-2 px-3 py-1 hover:bg-slate-700 text-sm group">
      {branch.isCurrent && <span className="text-green-400 text-xs font-bold">●</span>}
      <span className="flex-1 truncate">
        {branch.isCurrent ? branch.name : branch.name.replace(/^remotes\//, '')}
      </span>
      <span className="text-xs text-slate-500">{branch.shortName || branch.sha.slice(0, 7)}</span>
      <div className="hidden group-hover:flex gap-1">
        <button onClick={onCheckout} className="text-xs text-blue-400 hover:text-blue-300">Checkout</button>
        {branch.isLocal && !branch.isCurrent && (
          <>
            <button onClick={onRename} className="text-xs text-yellow-400 hover:text-yellow-300">Rename</button>
            <button onClick={onDelete} className="text-xs text-red-400 hover:text-red-300">Delete</button>
          </>
        )}
      </div>
    </div>
  )
}