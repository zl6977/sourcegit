import type { DiffLine } from '@sourcegit/shared'

interface DiffViewProps {
  diff: DiffLine[]
}

export function DiffView({ diff }: DiffViewProps) {
  if (!diff || diff.length === 0) {
    return <div className="text-slate-500 text-sm p-4">No diff available</div>
  }

  return (
    <div className="font-mono text-xs overflow-auto max-h-[600px]">
      {diff.map((line, i) => (
        <div
          key={i}
          className={`px-2 py-px whitespace-pre
            ${line.type === '+' ? 'bg-green-900/30 text-green-300' : ''}
            ${line.type === '-' ? 'bg-red-900/30 text-red-300' : ''}
            ${line.type === '@' ? 'bg-slate-600 text-slate-300 font-bold' : ''}
            ${line.type === ' ' ? 'text-slate-400' : ''}
          `}
        >
          <span className="w-12 text-right text-slate-600 select-none inline-block">
            {line.type === '+' ? '+' : line.type === '-' ? '-' : ' '}
          </span>
          {line.content}
        </div>
      ))}
    </div>
  )
}