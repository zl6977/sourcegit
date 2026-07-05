import { create } from 'zustand'
import type { RepoEntry } from '@sourcegit/shared'

interface RepoState {
  repos: RepoEntry[]
  currentRepoName: string | null
  addRepo: (name: string, path: string) => void
  removeRepo: (name: string) => void
  setCurrentRepo: (name: string | null) => void
  getRepoPath: (name: string) => string | undefined
}

export const useRepoStore = create<RepoState>((set, get) => ({
  repos: [],
  currentRepoName: null,

  addRepo: (name, path) => {
    const repos = get().repos
    const exists = repos.find(r => r.name === name)
    if (exists) {
      set({ repos: repos.map(r => r.name === name ? { name, path } : r) })
    } else {
      set({ repos: [...repos, { name, path }] })
    }
  },

  removeRepo: (name) => {
    const { repos, currentRepoName } = get()
    const newRepos = repos.filter(r => r.name !== name)
    set({
      repos: newRepos,
      currentRepoName: currentRepoName === name ? null : currentRepoName
    })
  },

  setCurrentRepo: (name) => {
    set({ currentRepoName: name })
  },

  getRepoPath: (name) => {
    const repo = get().repos.find(r => r.name === name)
    return repo?.path
  }
}))