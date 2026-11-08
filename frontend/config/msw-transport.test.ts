import net from 'node:net'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, expect, it } from 'vitest'
import { server } from '../src/test/msw.ts'

// Guarda do patch em patches/@mswjs__interceptors@0.45.8.patch (#397). O interceptor repassava à rede real
// a conexão cujo cliente já lê, mas ainda não escreveu, quando roda o setImmediate seguinte. Num processo
// carregado, a escrita do pedido HTTP do Undici perde essa corrida e o pedido escapa do mock.

type WriteArgs = [chunk: unknown, encoding: BufferEncoding, callback: (error?: Error | null) => void]
type SocketWrite = (this: net.Socket, ...args: WriteArgs) => void

const socketPrototype = net.Socket.prototype as unknown as { _write: SocketWrite }
const realWrite = socketPrototype._write
/** Fases "check" que a escrita espera, no máximo, quando nada chega à rede real (o caso correto). */
const MAX_HELD_TURNS = 50
const REAL_BODY = '{"mocked":false}'

let realServer: net.Server
let realConnections = 0
let releaseHeldWrites: () => void = () => undefined

beforeEach(async () => {
  realConnections = 0
  // A "rede real": se o interceptor repassar a conexão, o pedido chega aqui e recebe outra resposta.
  realServer = net.createServer((socket) => {
    realConnections += 1
    releaseHeldWrites()
    socket.once('data', () => {
      socket.end(
        `HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${String(REAL_BODY.length)}\r\nConnection: close\r\n\r\n${REAL_BODY}`,
      )
    })
  })
  await new Promise<void>((resolve) => realServer.listen(0, '127.0.0.1', resolve))
})

afterEach(async () => {
  socketPrototype._write = realWrite
  await new Promise<void>((resolve) =>
    realServer.close(() => {
      resolve()
    }),
  )
})

/**
 * Segura a primeira escrita de cada socket até a rede real receber a conexão ou até passarem
 * MAX_HELD_TURNS fases "check", sem relógio: o setImmediate do interceptor, agendado quando o cliente
 * passa a ler, sempre roda antes da escrita.
 */
function holdFirstSocketWrite(): { readonly held: () => number } {
  const seen = new WeakSet<net.Socket>()
  const pending = new Set<() => void>()
  let held = 0
  releaseHeldWrites = () => {
    for (const release of pending) release()
    pending.clear()
  }
  socketPrototype._write = function (...args) {
    if (seen.has(this)) {
      realWrite.apply(this, args)
      return
    }
    seen.add(this)
    held += 1
    let released = false
    const release = (): void => {
      if (released) return
      released = true
      pending.delete(release)
      realWrite.apply(this, args)
    }
    pending.add(release)
    const wait = (turns: number): void => {
      if (released) return
      if (turns === 0) release()
      else
        setImmediate(() => {
          wait(turns - 1)
        })
    }
    wait(MAX_HELD_TURNS)
  }
  return { held: () => held }
}

it('o pedido continua mockado quando a primeira escrita do socket chega depois do setImmediate', async () => {
  const address = realServer.address() as net.AddressInfo
  const url = `http://127.0.0.1:${String(address.port)}/transport-probe`
  server.use(http.get(url, () => HttpResponse.json({ mocked: true })))
  const hold = holdFirstSocketWrite()

  const response = await fetch(url)

  expect(hold.held()).toBeGreaterThan(0)
  expect(realConnections).toBe(0)
  expect(await response.json()).toEqual({ mocked: true })
})
