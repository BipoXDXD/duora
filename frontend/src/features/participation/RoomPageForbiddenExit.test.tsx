import { withSessionContext } from '@/test/private-context'
import { act, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Restricted',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}

function forbiddenRead(code: string) {
  return http.get(`${API}/rooms/room-1`, () =>
    HttpResponse.json(
      { type: 'about:blank', title: 'Forbidden', status: 403, code },
      { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  )
}

const ROOM_POLL_INTERVAL_MS = 2000

/** Conta na chamada de `fetch`, e não no handler do MSW, para a contagem não depender de a resposta já ter chegado. */
function roomReadsIn(fetchSpy: { readonly mock: { readonly calls: readonly unknown[][] } }): number {
  return fetchSpy.mock.calls.filter(([request]) => isRoomRead(request)).length
}

function isRoomRead(request: unknown): boolean {
  return (
    request instanceof Request && request.method === 'GET' && new URL(request.url).pathname === `${API}/rooms/room-1`
  )
}

async function advancePolling(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('saída da sala quando a leitura é recusada', () => {
  it('oferece a saída a quem deixou de ser elegível e confirma o encerramento', async () => {
    let exits = 0
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      forbiddenRead('FORBIDDEN'),
      http.post(`${API}/rooms/room-1/exit`, () => {
        exits += 1
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()

    const { user } = await renderApp('/rooms/room-1')
    await user.click(await screen.findByRole('button', { name: 'Sair da sala' }))

    expect(await screen.findByText('Este encontro foi encerrado.')).toBeInTheDocument()
    await waitFor(() => {
      expect(exits).toBe(1)
    })
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('para de consultar a sala depois que a saída é confirmada', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      forbiddenRead('FORBIDDEN'),
      http.post(`${API}/rooms/room-1/exit`, () => new HttpResponse(null, { status: 204 })),
    )
    useAntiforgeryEndpoint()

    const { user } = await renderApp('/rooms/room-1')
    await screen.findByRole('button', { name: 'Sair da sala' })
    const readsBeforeInterval = roomReadsIn(fetchSpy)
    await advancePolling(ROOM_POLL_INTERVAL_MS)
    expect(roomReadsIn(fetchSpy)).toBe(readsBeforeInterval + 1)

    // Sem leitura bem-sucedida, a consulta volta a "carregando" a cada intervalo e o botão é recriado ao fim dela.
    const exitButton = await screen.findByRole('button', { name: 'Sair da sala' })
    const readsAtExit = roomReadsIn(fetchSpy)
    await user.click(exitButton)
    expect(await screen.findByText('Este encontro foi encerrado.')).toBeInTheDocument()
    await advancePolling(ROOM_POLL_INTERVAL_MS * 3)

    expect(roomReadsIn(fetchSpy)).toBe(readsAtExit)
  })

  it('não oferece a saída a conta privilegiada, que a rota recusa', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(withSessionContext({ ...me, mfaEnabled: true, roles: ['moderator'] })),
      ),
      forbiddenRead('FORBIDDEN'),
    )
    useAntiforgeryEndpoint()

    await renderApp('/rooms/room-1')

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
  })

  it('não oferece a saída à conta suspensa, que o portão continua recusando', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      forbiddenRead('ACCOUNT_SUSPENDED'),
    )
    useAntiforgeryEndpoint()

    await renderApp('/rooms/room-1')

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
  })
})
