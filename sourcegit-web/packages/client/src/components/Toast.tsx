import { useEffect, useState } from 'react'

interface ToastMessage {
  id: number
  text: string
  type: 'info' | 'success' | 'error'
}
let toastId = 0

interface ToastProviderState {
  toasts: ToastMessage[]
  addToast: (text: string, type?: 'info' | 'success' | 'error') => void
  removeToast: (id: number) => void
}

let globalState: ToastProviderState = {
  toasts: [],
  addToast(text, type = 'info') {
    const id = ++toastId
    const toast: ToastMessage = { id, text, type }
    globalState.toasts = [...globalState.toasts, toast]
    notifyListeners()
    setTimeout(() => {
      globalState.removeToast(id)
    }, 3000)
  },
  removeToast(id) {
    globalState.toasts = globalState.toasts.filter(t => t.id !== id)
    notifyListeners()
  }
}

const listeners = new Set<() => void>()
function notifyListeners() {
  for (const fn of listeners) fn()
}

export function useToast(): ToastProviderState {
  const [toasts, setToasts] = useState<ToastMessage[]>(globalState.toasts)

  useEffect(() => {
    const unsub = () => {
      setToasts(globalState.toasts)
    }
    listeners.add(unsub)
    return () => { listeners.delete(unsub) }
  }, [])

  return {
    toasts,
    addToast: globalState.addToast,
    removeToast: globalState.removeToast
  }
}

export function Toast() {
  const { toasts, removeToast } = useToast()

  return (
    <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2">
      {toasts.map(toast => (
        <div
          key={toast.id}
          onClick={() => removeToast(toast.id)}
          className={`px-4 py-3 rounded-lg shadow-lg text-sm cursor-pointer min-w-[200px] transition-opacity
            ${toast.type === 'error' ? 'bg-red-600 text-white' : ''}
            ${toast.type === 'success' ? 'bg-green-600 text-white' : ''}
            ${toast.type === 'info' ? 'bg-slate-600 text-white' : ''}
          `}
        >
          {toast.text}
        </div>
      ))}
    </div>
  )
}