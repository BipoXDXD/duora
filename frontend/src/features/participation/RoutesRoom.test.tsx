import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const initial = {
  experienceId: 'unlikely-routes',
  rulesVersion: 1,
  contentVersion: 1,
  schemaVersion: 1,
  resourceVersion: '4',
  status: 'Active',
  deadline: '2026-10-22T18:00:00Z',
  stage: 'Introduction',
  title: 'Rotas Improváveis',
  objective: 'Criem um passeio juntos.',
  example: 'Uma praça e um café.',
  myClues: [] as string[],
  currentStop: null as null | { stepId: string; prompt: string; options: { optionId: string; label: string }[] },
  isMyTurn: false,
  route: [] as { stepId: string; prompt: string; optionLabel: string }[],
  revisionsRemaining: 1,
  awaitingPartner: false,
  availableActions: ['acknowledge'],
}
function setup(view = { ...initial }, state = 'Active') {
  const snapshot = { view, state }
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json({
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
        email: 'test@example.com',
        status: 'Active',
        emailConfirmed: true,
        mfaEnabled: false,
        roles: ['participant'],
      }),
    ),
    http.get(`${API}/rooms/routes`, () =>
      HttpResponse.json({
        roomId: 'routes',
        state: snapshot.state,
        version: '4',
        communicationLanguage: 'pt',
        communicationPolicyVersion: 1,
        displayLocale: 'pt-BR',
        invitationExpiresAt: '2026-10-22T18:00:00Z',
        game: null,
        routesGame: { ...snapshot.view, partnerClues: ['Segredo da outra pessoa.'] },
      }),
    ),
  )
  return snapshot
}
const stop = {
  stepId: 'first',
  prompt: 'Onde começar?',
  options: [
    { optionId: 'park', label: 'Parque' },
    { optionId: 'cafe', label: 'Café' },
  ],
}

describe('Routes room', () => {
  it('acknowledges both stages, shows only projected clues and completes through keyboard actions', async () => {
    const snapshot = setup()
    const getUserMedia = vi.fn()
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } })
    const actions: Record<string, unknown>[] = []
    server.use(
      http.post(`${API}/rooms/routes/routes-actions`, async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>
        actions.push(body)
        expect(request.headers.get('Idempotency-Key')).toBe(body.actionId)
        expect(body.expectedVersion).toBe('4')
        if (body.type === 'acknowledge' && body.stage === 'Introduction')
          snapshot.view = { ...initial, stage: 'Clues', myClues: ['Prefere sombra.'] }
        else if (body.type === 'acknowledge' && body.stage === 'Clues')
          snapshot.view = {
            ...snapshot.view,
            stage: 'Choices',
            isMyTurn: true,
            currentStop: stop,
            availableActions: ['choose-option'],
          }
        else if (body.type === 'choose-option')
          snapshot.view = {
            ...snapshot.view,
            stage: 'Adjustment',
            currentStop: null,
            route: [{ stepId: 'first', prompt: stop.prompt, optionLabel: 'Parque' }],
            availableActions: ['confirm-route', 'request-revision'],
          }
        else {
          snapshot.view = { ...snapshot.view, stage: 'Conclusion', status: 'Concluded', availableActions: [] }
          snapshot.state = 'Completed'
        }
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/routes')
    expect(await screen.findByText('Criem um passeio juntos.')).toBeInTheDocument()
    const acknowledge = screen.getByRole('button', { name: 'Entendi' })
    acknowledge.focus()
    await user.keyboard('{Enter}')
    expect(await screen.findByText('Prefere sombra.')).toBeInTheDocument()
    expect(screen.getByText('Só você vê estas pistas.')).toBeInTheDocument()
    expect(screen.queryByText('Segredo da outra pessoa.')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Entendi' }))
    const option = await screen.findByRole('radio', { name: 'Parque' })
    option.focus()
    await user.keyboard(' ')
    await user.click(screen.getByRole('button', { name: 'Escolher' }))
    await user.click(await screen.findByRole('button', { name: 'Confirmar roteiro' }))
    expect(await screen.findByText('Experiência concluída.')).toBeInTheDocument()
    expect(screen.getByText('Parque')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar roteiro' })).not.toBeInTheDocument()
    expect(actions.map((action) => [action.type, action.stage])).toEqual([
      ['acknowledge', 'Introduction'],
      ['acknowledge', 'Clues'],
      ['choose-option', undefined],
      ['confirm-route', undefined],
    ])
    expect(actions[2]).toMatchObject({ stepId: 'first', optionId: 'park' })
    expect(getUserMedia).not.toHaveBeenCalled()
  })
  it('shows partner turn in text and prevents choosing out of turn', async () => {
    setup({
      ...initial,
      stage: 'Choices',
      myClues: ['Minha pista.'],
      currentStop: stop,
      awaitingPartner: true,
      availableActions: [],
    })
    await renderApp('/rooms/routes')
    expect(await screen.findByText('É a vez da outra pessoa.')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Parque' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Escolher' })).toBeDisabled()
    expect(screen.queryByText('Escolha da outra pessoa')).not.toBeInTheDocument()
  })
  it('requests revision and disables it when no revisions remain', async () => {
    const snapshot = setup({ ...initial, stage: 'Adjustment', availableActions: ['request-revision'] })
    server.use(
      http.post(`${API}/rooms/routes/routes-actions`, async ({ request }) => {
        expect(await request.json()).toMatchObject({ type: 'request-revision' })
        snapshot.view = { ...snapshot.view, revisionsRemaining: 0, availableActions: [] }
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/routes')
    await user.click(await screen.findByRole('button', { name: 'Pedir revisão' }))
    expect(await screen.findByText('Revisões restantes: 0')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pedir revisão' })).toBeDisabled()
  })
  it('waits after acknowledgement and keeps the stage action unavailable', async () => {
    const snapshot = setup()
    server.use(
      http.post(`${API}/rooms/routes/routes-actions`, () => {
        snapshot.view = { ...snapshot.view, awaitingPartner: true, availableActions: [] }
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/routes')
    await user.click(await screen.findByRole('button', { name: 'Entendi' }))
    expect(await screen.findByText('Aguardando a outra pessoa confirmar.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Entendi' })).toBeDisabled()
  })
  it('does not expose actions for an expired room even with a stale active projection', async () => {
    setup(initial, 'Expired')
    await renderApp('/rooms/routes')
    expect(await screen.findByText('O prazo terminou. Veja abaixo o progresso realizado.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Entendi' })).not.toBeInTheDocument()
  })
})
