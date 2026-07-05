import { useEffect, useRef, useCallback } from 'react'

/** Local types for the WebSocket operation protocol. */
export interface GitOperation {
  name: string
  input: unknown
  correlationId?: string
}

export interface GitOperationResult {
  success: boolean
  data: unknown
  error?: string
  correlationId?: string
}

let sharedSocket: WebSocket | null = null
let socketReady = false

function getSocket(): WebSocket {
  if (sharedSocket && sharedSocket.readyState === WebSocket.OPEN) {
    socketReady = true
    return sharedSocket
  }

  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  sharedSocket = new WebSocket(`${proto}//${window.location.host}/ws`)

  sharedSocket.onopen = () => { socketReady = true }
  sharedSocket.onclose = () => {
    socketReady = false
    sharedSocket = null
  }

  return sharedSocket
}

interface OperationHook {
  execute: <T = unknown>(operation: GitOperation) => Promise<T>
}

export function useGitOperation(): OperationHook {
  const handlersRef = useRef<Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>>(new Map())

  useEffect(() => {
    const socket = getSocket()

    const handleMsg = (e: MessageEvent) => {
      let data: GitOperationResult & { correlationId?: string }
      try {
        data = JSON.parse(e.data)
      } catch {
        return
      }

      if (data.correlationId) {
        const handler = handlersRef.current.get(data.correlationId)
        if (handler) {
          handlersRef.current.delete(data.correlationId)
          if (data.success) {
            handler.resolve(data.data)
          } else {
            handler.reject(new Error(data.error || 'Operation failed'))
          }
        }
      }
    }

    socket.addEventListener('message', handleMsg)

    return () => {
      socket.removeEventListener('message', handleMsg)
    }
  }, [])

  const execute = useCallback(<T = unknown>(operation: GitOperation): Promise<T> => {
    return new Promise((resolve, reject) => {
      const socket = getSocket()
      const correlationId = `${Date.now()}-${Math.random().toString(36).slice(2)}`

      const handler = { resolve: resolve as (v: unknown) => void, reject }
      handlersRef.current.set(correlationId, handler)

      const message = JSON.stringify({ ...operation, correlationId })

      if (socket.readyState === WebSocket.OPEN) {
        socket.send(message)
      } else {
        socket.onopen = () => {
          socket.send(message)
        }
      }

      setTimeout(() => {
        if (handlersRef.current.has(correlationId)) {
          handlersRef.current.delete(correlationId)
          reject(new Error('Operation timed out'))
        }
      }, 30000)
    })
  }, [])

  return { execute }
}