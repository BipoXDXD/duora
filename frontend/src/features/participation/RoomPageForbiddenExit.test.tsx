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

const LOADING_TEXT = 'Carregando suas preferências…'
const UNAVAILABLE_TEXT = 'Este encontro não está disponível.'
const EXIT_BUTTON = { name: 'Sair da sala' }
const ACCEPT_BUTTON = { name: 'Aceitar convite' }

function problemResponse(status: number, code: string): Response {
  return HttpResponse.json(
    { type: 'about:blank', title: 'Refused', status, code },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

function invitedRoomResponse(): Response {
  return HttpResponse.json({
    roomId: 'room-1',
    state: 'Invited',
    version: '1',
    communicationLanguage: 'pt',
    communicationPolicyVersion: 1,
    displayLocale: 'pt-BR',
    invitationExpiresAt: '2026-10-19T18:00:00Z',
    game: null,
  })
}

/** Resposta que o teste libera quando quer, para observar a tela com a leitura ainda em andamento. */
function pendingResponse(): { readonly promise: Promise<Response>; readonly release: (response: Response) => void } {
  let release: (response: Response) => void = () => undefined
  const promise = new Promise<Response>((resolve) => {
    release = resolve
  })
  return { promise, release }
}

/** Entrega uma resposta por leitura, em ordem; depois da última, repete-a como faria o servidor a cada intervalo. */
function roomReads(...responses: readonly (() => Response | Promise<Response>)[]) {
  let index = 0
  return http.get(`${API}/rooms/room-1`, () => {
    const respond = responses[Math.min(index, responses.length - 1)]
    index += 1
    return respond?.()
  })
}

function useSignedInParticipant(): void {
  server.use(http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))))
  useAntiforgeryEndpoint()
}

/** O TanStack Query entrega a mudança de estado num `setTimeout(0)` real, que só roda depois de o intervalo disparar. */
async function advancePollingAndNotify(): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ROOM_POLL_INTERVAL_MS)
    await new Promise((resolve) => setTimeout(resolve, 0))
  })
}

function startPolling() {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] })
  return vi.spyOn(globalThis, 'fetch')
}

describe('tela da sala enquanto a próxima leitura não responde', () => {
  it('mostra "carregando", sem aviso nem saída, até a primeira leitura responder', async () => {
    const firstRead = pendingResponse()
    useSignedInParticipant()
    server.use(roomReads(() => firstRead.promise))

    await renderApp('/rooms/room-1')

    expect(await screen.findByText(LOADING_TEXT)).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', EXIT_BUTTON)).not.toBeInTheDocument()

    firstRead.release(problemResponse(403, 'FORBIDDEN'))

    expect(await screen.findByRole('button', EXIT_BUTTON)).toBeInTheDocument()
    expect(screen.queryByText(LOADING_TEXT)).not.toBeInTheDocument()
  })

  it('mantém o mesmo botão de saída e não volta a "carregando" durante o refetch', async () => {
    const fetchSpy = startPolling()
    const secondRead = pendingResponse()
    useSignedInParticipant()
    server.use(
      roomReads(
        () => problemResponse(403, 'FORBIDDEN'),
        () => secondRead.promise,
      ),
    )

    await renderApp('/rooms/room-1')
    const exitButton = await screen.findByRole('button', EXIT_BUTTON)
    const readsBeforeInterval = roomReadsIn(fetchSpy)
    await advancePollingAndNotify()

    expect(roomReadsIn(fetchSpy)).toBe(readsBeforeInterval + 1)
    expect(screen.queryByText(LOADING_TEXT)).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', EXIT_BUTTON)).toBe(exitButton)

    secondRead.release(problemResponse(403, 'FORBIDDEN'))
  })

  it('aceita o clique na saída durante o refetch', async () => {
    startPolling()
    const secondRead = pendingResponse()
    let exits = 0
    useSignedInParticipant()
    server.use(
      roomReads(
        () => problemResponse(403, 'FORBIDDEN'),
        () => secondRead.promise,
      ),
      http.post(`${API}/rooms/room-1/exit`, () => {
        exits += 1
        return new HttpResponse(null, { status: 204 })
      }),
    )

    const { user } = await renderApp('/rooms/room-1')
    await screen.findByRole('button', EXIT_BUTTON)
    await advancePollingAndNotify()
    await user.click(screen.getByRole('button', EXIT_BUTTON))

    expect(await screen.findByText('Este encontro foi encerrado.')).toBeInTheDocument()
    expect(exits).toBe(1)

    secondRead.release(problemResponse(403, 'FORBIDDEN'))
  })

  it('mantém o aviso de sala não encontrada e não volta a "carregando" durante o refetch', async () => {
    const fetchSpy = startPolling()
    const secondRead = pendingResponse()
    useSignedInParticipant()
    server.use(
      roomReads(
        () => problemResponse(404, 'NOT_FOUND'),
        () => secondRead.promise,
      ),
    )

    await renderApp('/rooms/room-1')
    const notice = await screen.findByText(UNAVAILABLE_TEXT)
    const readsBeforeInterval = roomReadsIn(fetchSpy)
    await advancePollingAndNotify()

    expect(roomReadsIn(fetchSpy)).toBe(readsBeforeInterval + 1)
    expect(screen.queryByText(LOADING_TEXT)).not.toBeInTheDocument()
    expect(screen.getByText(UNAVAILABLE_TEXT)).toBe(notice)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    secondRead.release(problemResponse(404, 'NOT_FOUND'))
  })

  it('tira a saída quando a recusa de 403 vira 404', async () => {
    startPolling()
    useSignedInParticipant()
    server.use(
      roomReads(
        () => problemResponse(403, 'FORBIDDEN'),
        () => problemResponse(404, 'NOT_FOUND'),
      ),
    )

    await renderApp('/rooms/room-1')
    const exitButton = await screen.findByRole('button', EXIT_BUTTON)
    await advancePollingAndNotify()

    await waitFor(() => {
      expect(exitButton).not.toBeInTheDocument()
    })
    expect(screen.getByText(UNAVAILABLE_TEXT)).toBeInTheDocument()
    expect(screen.queryByText(LOADING_TEXT)).not.toBeInTheDocument()
  })

  it('tira a saída quando a recusa de 403 vira restrição de sessão', async () => {
    startPolling()
    useSignedInParticipant()
    server.use(
      roomReads(
        () => problemResponse(403, 'FORBIDDEN'),
        () => problemResponse(403, 'ACCOUNT_SUSPENDED'),
      ),
    )

    await renderApp('/rooms/room-1')
    const exitButton = await screen.findByRole('button', EXIT_BUTTON)
    await advancePollingAndNotify()

    await waitFor(() => {
      expect(exitButton).not.toBeInTheDocument()
    })
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })

  it('mostra a sala e tira o aviso quando a leitura volta a funcionar', async () => {
    startPolling()
    useSignedInParticipant()
    server.use(roomReads(() => problemResponse(403, 'FORBIDDEN'), invitedRoomResponse))

    await renderApp('/rooms/room-1')
    await screen.findByRole('button', EXIT_BUTTON)
    await advancePollingAndNotify()

    expect(await screen.findByRole('button', ACCEPT_BUTTON)).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', EXIT_BUTTON)).not.toBeInTheDocument()
  })

  it('revalida a identidade quando a leitura traz restrição de sessão', async () => {
    let identityReads = 0
    useAntiforgeryEndpoint()
    server.use(
      http.get(`${API}/me`, () => {
        identityReads += 1
        return HttpResponse.json(withSessionContext(me))
      }),
      roomReads(() => problemResponse(403, 'ACCOUNT_SUSPENDED')),
    )

    await renderApp('/rooms/room-1')

    await waitFor(() => {
      expect(identityReads).toBeGreaterThan(1)
    })
  })

  it('revalida a identidade quando a leitura traz 401', async () => {
    let identityReads = 0
    useAntiforgeryEndpoint()
    server.use(
      http.get(`${API}/me`, () => {
        identityReads += 1
        return HttpResponse.json(withSessionContext(me))
      }),
      roomReads(() => problemResponse(401, 'UNAUTHENTICATED')),
    )

    await renderApp('/rooms/room-1')

    await waitFor(() => {
      expect(identityReads).toBeGreaterThan(1)
    })
  })

  it('repete a leitura que falha com 503, sem esperar o próximo intervalo', async () => {
    const fetchSpy = startPolling()
    useSignedInParticipant()
    server.use(roomReads(() => problemResponse(503, 'SERVICE_UNAVAILABLE'), invitedRoomResponse))

    await renderApp('/rooms/room-1')

    expect(await screen.findByRole('button', ACCEPT_BUTTON)).toBeInTheDocument()
    expect(roomReadsIn(fetchSpy)).toBe(2)
  })
})
