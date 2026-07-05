import { useEffect } from 'react'
import { Routes, Route, useNavigate, useParams, useLocation } from 'react-router-dom'
import { useRepoStore } from './stores/repoStore'
import Welcome from './pages/Welcome'
import WorkingCopy from './pages/WorkingCopy'
import History from './pages/History'
import Branches from './pages/Branches'
import Stashes from './pages/Stashes'
import { Toast } from './components/Toast'

function RepoContent() {
  const { name: repoName } = useParams<{ name: string }>()
  const navigate = useNavigate()
  const location = useLocation()

  return (
    <div className="flex h-screen">
      {/* Sidebar */}
      <div className="w-48 bg-slate-800 border-r border-slate-700 flex flex-col">
        <div className="p-3 border-b border-slate-700">
          <h2 className="text-sm font-bold text-white truncate">{repoName}</h2>
        </div>
        <nav className="flex-1 py-2">
          <RepoNavLink name={repoName!} path="" isActive={!location.pathname.includes('/repos/' + repoName + '/') || location.pathname === '/repos/' + repoName}>
            Working Copy
          </RepoNavLink>
          <RepoNavLink name={repoName!} path="/history" isActive={location.pathname.includes('/repos/' + repoName + '/history')}>
            History
          </RepoNavLink>
          <RepoNavLink name={repoName!} path="/branches" isActive={location.pathname.includes('/repos/' + repoName + '/branches')}>
            Branches
          </RepoNavLink>
          <RepoNavLink name={repoName!} path="/stashes" isActive={location.pathname.includes('/repos/' + repoName + '/stashes')}>
            Stashes
          </RepoNavLink>
        </nav>
      </div>

      {/* Main content */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <Routes>
          <Route index element={<WorkingCopy />} />
          <Route path="history" element={<History />} />
          <Route path="branches" element={<Branches />} />
          <Route path="stashes" element={<Stashes />} />
        </Routes>
      </div>
    </div>
  )
}

function RepoNavLink({ name, path, isActive, children }: { name: string; path: string; isActive: boolean; children: string }) {
  const navigate = useNavigate()
  return (
    <button
      onClick={() => navigate(`/repos/${name}${path}`)}
      className={`w-full text-left px-3 py-2 text-xs transition-colors
        ${isActive ? 'bg-slate-700 text-white' : 'text-slate-400 hover:text-slate-200 hover:bg-slate-700/50'}
      `}
    >
      {children}
    </button>
  )
}

export default function App() {
  const addRepo = useRepoStore(state => state.addRepo)

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

  return (
    <div className="flex flex-col h-screen bg-slate-900 text-slate-200">
      <Routes>
        <Route path="/" element={<Welcome />} />
        <Route path="/repos/:name/*" element={<RepoContent />} />
      </Routes>
      <Toast />
    </div>
  )
}