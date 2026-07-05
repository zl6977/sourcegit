import { WebSocketServer, WebSocket } from 'ws'
import type { IncomingMessage } from 'http'
import { resolveRepoParam } from './router.js'

interface WsMessage {
  type: 'request' | 'subscribe'
  id?: string
  repoId: string
  operation: string
  input?: Record<string, unknown>
}

interface WsResponse {
  type: 'response' | 'error' | 'stdout' | 'stderr'
  id?: string
  success: boolean
  error?: string
  data?: unknown
}

export function setupWebSocket(
  wss: WebSocketServer,
): void {
  wss.on('connection', (ws: WebSocket, req: IncomingMessage) => {
    // Parse repoId from URL query string
    const url = new URL(req.url ?? '/', `http://${req.headers.host}`)
    const repoId = url.searchParams.get('repoId')

    if (!repoId) {
      ws.send(JSON.stringify({
        type: 'error',
        success: false,
        error: 'repoId query parameter is required',
      } as WsResponse))
      ws.close(1008, 'Missing repoId')
      return
    }

    // Check if repo exists
    const record = resolveRepoParam(repoId)
    if (!record) {
      ws.send(JSON.stringify({
        type: 'error',
        success: false,
        error: `Repository not found: ${repoId}`,
      } as WsResponse))
      ws.close(1008, 'Repository not found')
      return
    }

    // Send connected acknowledgment
    ws.send(JSON.stringify({
      type: 'response',
      success: true,
      data: { connected: true, repo: { name: record.name, path: record.path } },
    } as WsResponse))

    ws.on('message', async (data: WebSocket.Data) => {
      try {
        const msg: WsMessage = JSON.parse(data.toString())
        await handleMessage(ws, msg, record)
      } catch (err) {
        const error = err instanceof Error ? err.message : String(err)
        ws.send(JSON.stringify({
          type: 'error',
          success: false,
          error: `Failed to parse message: ${error}`,
        } as WsResponse))
      }
    })

    ws.on('close', () => {
      // Cleanup if needed
    })

    ws.on('error', (err) => {
      // Log but don't crash
    })
  })
}

async function handleMessage(
  ws: WebSocket,
  msg: WsMessage,
  record: ReturnType<typeof resolveRepoParam>,
): Promise<void> {
  if (!record) {
    ws.send(JSON.stringify({
      type: 'error',
      success: false,
      error: 'Repository not found',
      id: msg.id,
    } as WsResponse))
    return
  }

  switch (msg.type) {
    case 'request':
      await handleRequest(ws, msg, record)
      break

    case 'subscribe':
      // For streaming operations, send stdout/stderr events
      await handleSubscribe(ws, msg, record)
      break

    default:
      ws.send(JSON.stringify({
        type: 'error',
        success: false,
        error: `Unknown message type: ${msg.type}`,
        id: msg.id,
      } as WsResponse))
  }
}

async function handleRequest(
  ws: WebSocket,
  msg: WsMessage,
  record: ReturnType<typeof resolveRepoParam>,
): Promise<void> {
  if (!record) return

  const name = msg.operation

  try {
    const result = await record.registry.dispatch(
      name,
      record.path,
      { executor: record.executor },
      msg.input,
    )

    ws.send(JSON.stringify({
      type: 'response',
      id: msg.id,
      success: result.success,
      error: result.error,
      data: result.data,
    } as WsResponse))
  } catch (err) {
    const error = err instanceof Error ? err.message : String(err)
    ws.send(JSON.stringify({
      type: 'error',
      id: msg.id,
      success: false,
      error,
    } as WsResponse))
  }
}

async function handleSubscribe(
  ws: WebSocket,
  msg: WsMessage,
  record: ReturnType<typeof resolveRepoParam>,
): Promise<void> {
  if (!record) return

  const name = msg.operation

  try {
    const result = await record.registry.dispatch(
      name,
      record.path,
      { executor: record.executor },
      msg.input,
    )
    ws.send(JSON.stringify({
      type: 'response',
      id: msg.id,
      success: result.success,
      error: result.error,
      data: result.data,
    } as WsResponse))
  } catch (err) {
    const error = err instanceof Error ? err.message : String(err)
    ws.send(JSON.stringify({
      type: 'error',
      id: msg.id,
      success: false,
      error,
    } as WsResponse))
  }
}

export { handleMessage }