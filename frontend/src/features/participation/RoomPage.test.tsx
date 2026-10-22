import { screen } from '@testing-library/react'
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

describe('localized room', () => {
  it('displays agreed language and completes by text without requesting a microphone', async () => {
    let completed = false
    const getUserMedia = vi.fn()
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(me)),
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
      http.get(`${API}/me`, () => HttpResponse.json(me)),
      http.get(`${API}/rooms/room-2`, () =>
        HttpResponse.json({
          roomId: 'room-2',
          state: rejected ? 'Aborted' : 'Invited',
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
    await screen.findByText('Este encontro foi encerrado.')
  })
})
