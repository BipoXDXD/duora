import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
const game = {
  deadline: '2026-10-19T18:08:00Z',
  title: 'Um encontro',
  objective: 'Escolham juntos',
  currentCard: {
    cardId: 'outing',
    position: 1,
    prompt: 'Onde passear?',
    options: [{ optionId: 'park', label: 'Parque' }],
  },
  awaitingPartner: false,
  revealed: [],
  passed: [],
  availableActions: ['choose-option', 'skip-card'],
}

function notFound(traceId: string): Response {
  return HttpResponse.json(
    { type: 'urn:duora:problem:not-found', title: 'Not Found', status: 404, traceId },
    { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

const INVITATION_UNAVAILABLE = 'Este convite não está mais disponível.'

/** Sala em convite cujo estado o teste muda dentro do handler do comando, como o servidor faria. */
function useInvitationRoom(roomId: string): { state: string } {
  const snapshot = { state: 'Invited' }
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/rooms/${roomId}`, () =>
      HttpResponse.json({
        roomId,
        state: snapshot.state,
        version: '1',
        communicationLanguage: 'pt',
        communicationPolicyVersion: 1,
        displayLocale: 'pt-BR',
        invitationExpiresAt: '2026-10-19T18:00:00Z',
        game: null,
      }),
    ),
  )
  useAntiforgeryEndpoint()
  return snapshot
}

describe('localized room', () => {
  it('displays agreed language and completes by text without requesting a microphone', async () => {
    let completed = false
    const getUserMedia = vi.fn()
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/rooms/room-1`, () =>
        HttpResponse.json({
          roomId: 'room-1',
          state: completed ? 'Completed' : 'Active',
          version: '2',
          communicationLanguage: 'en',
          communicationPolicyVersion: 1,
          displayLocale: 'pt-BR',
          invitationExpiresAt: '2026-10-19T18:00:00Z',
          game: completed ? { ...game, currentCard: null, availableActions: [] } : game,
        }),
      ),
      http.post(`${API}/rooms/room-1/actions`, async ({ request }) => {
        const action = await request.json()
        expect(action).toMatchObject({
          type: 'choose-option',
          cardId: 'outing',
          optionId: 'park',
          expectedVersion: '2',
        })
        completed = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1')
    expect(await screen.findByText('Idioma combinado: Inglês')).toBeInTheDocument()
    expect(screen.getByText(/Chat e voz ao vivo não têm tradução automática/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Parque' }))
    await screen.findByText('Experiência concluída.')
    expect(getUserMedia).not.toHaveBeenCalled()
  })
  it('requires an explicit invitation acceptance and allows rejection', async () => {
    let rejected = false
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/rooms/room-2`, () =>
        rejected
          ? notFound('00-rejected')
          : HttpResponse.json({
              roomId: 'room-2',
              state: 'Invited',
              version: '1',
              communicationLanguage: 'pt',
              communicationPolicyVersion: 1,
              displayLocale: 'pt-BR',
              invitationExpiresAt: '2026-10-19T18:00:00Z',
              game: null,
            }),
      ),
      http.post(`${API}/rooms/room-2/reject`, () => {
        rejected = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-2')
    expect(await screen.findByRole('button', { name: 'Aceitar convite' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Recusar convite' }))
    await screen.findByText('Esta sala não está disponível.')
  })
  // O 404 do aceite vencido não pode deixar o convite na tela até a próxima leitura periódica.
  it.each([
    ['accept', 'Aceitar convite'],
    ['reject', 'Recusar convite'],
  ])('shows a single notice after a late %s', async (command, button) => {
    const snapshot = useInvitationRoom('room-late')
    server.use(
      http.post(`${API}/rooms/room-late/${command}`, () => {
        snapshot.state = 'Expired'
        return notFound('00-late')
      }),
    )
    const { user } = await renderApp('/rooms/room-late')
    await user.click(await screen.findByRole('button', { name: button }))

    const notice = await screen.findByText(INVITATION_UNAVAILABLE)
    expect(notice).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('button', { name: 'Aceitar convite' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText('Esta sala foi encerrada.')).not.toBeInTheDocument()
    expect(screen.queryByText('Esta sala não está disponível.')).not.toBeInTheDocument()
  })
  // Os botões somem com o foco dentro; o aviso que os substitui recebe o foco (WCAG 2.4.3).
  it('moves focus to the notice when the invitation buttons go away', async () => {
    const snapshot = useInvitationRoom('room-late')
    server.use(
      http.post(`${API}/rooms/room-late/accept`, () => {
        snapshot.state = 'Expired'
        return notFound('00-late')
      }),
    )
    const { user } = await renderApp('/rooms/room-late')
    await user.click(await screen.findByRole('button', { name: 'Aceitar convite' }))

    const notice = await screen.findByText(INVITATION_UNAVAILABLE)
    await waitFor(() => {
      expect(notice).toHaveFocus()
    })
  })
  it('shows the late invitation notice in English', async () => {
    const snapshot = useInvitationRoom('room-late')
    server.use(
      http.post(`${API}/rooms/room-late/accept`, () => {
        snapshot.state = 'Expired'
        return notFound('00-late')
      }),
    )
    const { user } = await renderApp('/rooms/room-late', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: 'Accept invitation' }))

    expect(await screen.findByText('This invitation is no longer available.')).toHaveAttribute('role', 'status')
    expect(screen.queryByText('This room has been closed.')).not.toBeInTheDocument()
  })
  // Aba antiga: a sala já começou por outro caminho; a falha do convite não descreve mais a tela.
  it('drops the invitation failure once the room is active', async () => {
    const snapshot = useInvitationRoom('room-late')
    server.use(
      http.post(`${API}/rooms/room-late/reject`, () => {
        snapshot.state = 'Active'
        return notFound('00-stale-tab')
      }),
    )
    const { user } = await renderApp('/rooms/room-late')
    await user.click(await screen.findByRole('button', { name: 'Recusar convite' }))

    expect(await screen.findByRole('button', { name: 'Sair da sala' })).toBeInTheDocument()
    expect(screen.queryByText(INVITATION_UNAVAILABLE)).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  // A resposta se perdeu, mas o servidor aceitou: o alerta de conexão não pode ficar preso durante o jogo.
  it('drops a network failure once the accepted room is active', async () => {
    const snapshot = useInvitationRoom('room-late')
    server.use(
      http.post(`${API}/rooms/room-late/accept`, () => {
        snapshot.state = 'Active'
        return HttpResponse.error()
      }),
    )
    const { user } = await renderApp('/rooms/room-late')
    await user.click(await screen.findByRole('button', { name: 'Aceitar convite' }))

    expect(await screen.findByRole('button', { name: 'Sair da sala' })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  // Saída, suspensão e bloqueio chegam como a mesma resposta; só o traceId difere entre elas.
  it.each(['00-saida', '00-suspensao', '00-bloqueio'])(
    'shows the same neutral notice for an unavailable room (%s)',
    async (traceId) => {
      server.use(
        http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
        http.get(`${API}/rooms/room-gone`, () => notFound(traceId)),
      )
      await renderApp('/rooms/room-gone')
      expect(await screen.findByText('Esta sala não está disponível.')).toHaveAttribute('role', 'status')
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Voltar aos encontros' })).toBeInTheDocument()
    },
  )
  it('shows the unavailable notice in English', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/rooms/room-gone`, () => notFound('00-en')),
    )
    await renderApp('/rooms/room-gone', undefined, 'en-US')
    expect(await screen.findByText('This room is not available.')).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

it('áudio privado não instala src nativo nem baixa bytes antes do gesto', async () => {
  let reads = 0
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/rooms/room-audio`, () =>
      HttpResponse.json({
        roomId: 'room-audio',
        state: 'Active',
        version: '1',
        communicationLanguage: 'pt',
        communicationPolicyVersion: 1,
        displayLocale: 'pt-BR',
        invitationExpiresAt: '2026-10-19T18:00:00Z',
        game: null,
        audio: [
          {
            cueId: 'cue',
            assetId: 'asset',
            transcript: 'Transcrição acessível',
            accessibleDescription: 'Descrição acessível',
            sha256: 'a'.repeat(64),
            url: '/api/v1/rooms/room-audio/audio/cue',
          },
        ],
      }),
    ),
    http.get(`${API}/rooms/room-audio/audio/cue`, () => {
      reads += 1
      return new HttpResponse(null, { status: 403 })
    }),
  )
  await renderApp('/rooms/room-audio')
  await screen.findByText('Transcrição acessível')
  expect(document.querySelector('audio, source[src], [src^="blob:"], [src^="data:audio/"]')).toBeNull()
  expect(screen.getByRole('button', { name: 'Reproduzir áudio' })).toBeInTheDocument()
  expect(reads).toBe(0)
})
