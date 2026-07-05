import type { FileChange } from '@sourcegit/shared'

interface FileTreeProps {
  files: FileChange[]
  selected: string | null
  onSelect: (path: string) => void
  emptyText?: string
}

export function FileTree({ files, selected, onSelect, emptyText = 'No files' }: FileTreeProps) {
  if (files.length === 0) {
    return <div className="text-slate-500 text-sm py-2">{emptyText}</div>
  }

  return (
    <div className="flex flex-col">
      {files.map(file => (
        <div
          key={file.path}
          onClick={() => onSelect(file.path)}
          className={`flex items-center gap-2 px-2 py-1 text-sm cursor-pointer hover:bg-slate-700
            ${selected === file.path ? 'bg-slate-600' : ''}
          `}
        >
          <span className="w-4 text-center shrink-0">
            {getStatusIcon(file.status)}
          </span>
          <span className="truncate">{file.path}</span>
        </div>
      ))}
    </div>
  )
}

function getStatusIcon(status: string): JSX.Element {
  switch (status) {
    case 'A': return <span className="text-green-400">A</span>
    case 'M': return <span className="text-yellow-400">M</span>
    case 'D': return <span className="text-red-400">D</span>
    case 'R': return <span className="text-blue-400">R</span>
    case 'U': return <span className="text-purple-400">?</span>
    default: return <span className="text-slate-400">?</span>
  }
}