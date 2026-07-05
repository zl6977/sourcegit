import { useParams } from 'react-router-dom'
import { useRepoStore } from '../stores/repoStore'

export function useRepo(): { repoName: string; repoPath: string | undefined } {
  const { name } = useParams<{ name: string }>()
  const repos = useRepoStore(state => state.repos)

  const repoPath = repos.find(r => r.name === name)?.path

  return { repoName: name!, repoPath }
}