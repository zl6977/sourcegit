import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useRepoStore } from '../stores/repoStore'

interface WelcomeProps {}

export default function Welcome() {
  const navigate = useNavigate()
  const addRepo = useRepoStore(state => state.addRepo)
  const setCurrentRepo = useRepoStore(state => state.setCurrentRepo)

  useEffect(() => {
    fetch('/api/repos')
      .then(res => res.json())
      .then((data: { repos: string[] }) => {
        for (const path of data.repos) {
          const name = path.split(/[\/\\]/).pop() || path
          addRepo(name, path)
        }
      })
      .catch(() => {})
  }, [addRepo])

  const handleOpen = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const input = form.elements.namedItem('repoPath') as HTMLInputElement
    const path = input.value.trim()
    if (!path) return

    const name = path.split(/[\/\\]/).pop() || path
    addRepo(name, path)
    setCurrentRepo(name)
    navigate(`/repos/${encodeURIComponent(name)}`)
  }

  return (
    <div className="flex items-center justify-center min-h-screen bg-slate-900">
      <div className="w-full max-w-md p-8">
        <h1 className="text-3xl font-bold text-white mb-2 text-center">SourceGit Web</h1>
        <p className="text-slate-400 text-center mb-8">Open a Git repository</p>
        <form onSubmit={handleOpen} className="flex flex-col gap-4">
          <input
            name="repoPath"
            type="text"
            placeholder="Repository path..."
            className="bg-slate-800 text-slate-200 px-4 py-3 rounded-lg border border-slate-600 focus:outline-none focus:border-blue-500 text-sm"
          />
          <button
            type="submit"
            className="bg-blue-600 text-white py-3 rounded-lg hover:bg-blue-700 text-sm font-medium transition-colors"
          >
            Open
          </button>
        </form>
      </div>
    </div>
  )
}