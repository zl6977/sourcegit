import express from 'express'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createServer } from 'node:http'
import { WebSocketServer } from 'ws'
import { router, closeAllBackends } from './router.js'
import { setupWebSocket } from './websocket.js'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

const PORT = parseInt(process.env.PORT ?? '3000', 10)
const IS_PROD = process.env.NODE_ENV === 'production'

const app = express()
const server = createServer(app)

// JSON body parser
app.use(express.json({ limit: '1mb' }))

// Git REST API routes
app.use('/api/git', router)

// In production, serve the client build
if (IS_PROD) {
  const clientDist = path.resolve(__dirname, '../../client/dist')
  app.use(express.static(clientDist))

  // SPA fallback: serve index.html for any non-API route
  app.get('*', (_req, res) => {
    res.sendFile(path.join(clientDist, 'index.html'))
  })
}

// WebSocket server
const wss = new WebSocketServer({ server })
setupWebSocket(wss)

// Start server
server.listen(PORT, () => {
  console.log(`SourceGit server listening on port ${PORT}`)
  if (IS_PROD) {
    console.log('Serving client build from dist/')
  }
})

// Graceful shutdown
process.on('SIGINT', () => {
  console.log('Shutting down...')
  closeAllBackends()
  server.close(() => {
    process.exit(0)
  })
})

process.on('SIGTERM', () => {
  console.log('Shutting down...')
  closeAllBackends()
  server.close(() => {
    process.exit(0)
  })
})

export { app, server, wss }