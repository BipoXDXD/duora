import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const deadline = '2026-10-22T18:00:00Z'
const card = { cardId: 'outing', position: 1, prompt: 'Where to go?', options: [{ optionId: 'park', label: 'Park' }] }
const constellation = {
  title: 'Choices',
  objective: 'Choose together',
  deadline,
  currentCard: card,
  awaitingPartner: false,
  revealed: [],
  passed: [],
  availableActions: ['choose-option', 'skip-card'],
}
const routes = {
  experienceId: 'unlikely-routes',
  rulesVersion: 1,
  contentVersion: 1,
  schemaVersion: 1,
  resourceVersion: '4',
  status: 'Active',
  deadline,
  stage: 'Introduction',
  title: 'Routes',
  objective: 'Plan together',
  example: 'An island',
  myClues: [],
  currentStop: null,
  isMyTurn: false,
  route: [],
  revisionsRemaining: 1,
  awaitingPartner: false,
  availableActions: ['acknowledge'],
}
const kinds = {
  constellation: { path: 'actions', button: 'Park', game: constellation, routesGame: null },
  routes: { path: 'routes-actions', button: 'Got it', game: null, routesGame: routes },
}

function problem(status: number): Response {
  return HttpResponse.json(
    { type: 'urn:duora:problem', title: 'Problem', status, traceId: '00-trace' },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

/**
 * Por padrão a releitura continua devolvendo `Active`: o job de prazo ainda não rodou (#462).
 * Com `expiresOnReread`, a primeira leitura é `Active` e as seguintes `Expired`.
 */
function setup(kind: keyof typeof kinds, actionStatus: number, expiresOnReread = false) {
  const { path, game, routesGame } = kinds[kind]
  let reads = 0
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(
        withSessionContext({
          id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
          email: 'test@example.com',
          status: 'Active',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
        }),
      ),
    ),
    http.get(`${API}/rooms/room-1`, () =>
      HttpResponse.json({
        roomId: 'room-1',
        state: expiresOnReread && reads++ > 0 ? 'Expired' : 'Active',
        version: '2',
        communicationLanguage: 'en',
        communicationPolicyVersion: 1,
        displayLocale: 'en-US',
        invitationExpiresAt: deadline,
        game,
        routesGame,
        audio: [],
      }),
    ),
    http.post(`${API}/rooms/room-1/${path}`, () => problem(actionStatus)),
  )
}

describe.each(Object.keys(kinds) as (keyof typeof kinds)[])('%s action after the deadline', (kind) => {
  it('404 shows the choice-not-saved notice and hides the actions even when the reread is still Active', async () => {
    setup(kind, 404)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: kinds[kind].button }))
    expect(await screen.findByText("Your last choice wasn't saved.")).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText('This room has been closed.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: kinds[kind].button })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Leave room' })).not.toBeInTheDocument()
  })
  it('404 hides the actions before the reread resolves', async () => {
    setup(kind, 404)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    const button = await screen.findByRole('button', { name: kinds[kind].button })
    let releaseReread = (): void => undefined
    const rereadHeld = new Promise<void>((resolve) => {
      releaseReread = resolve
    })
    // Sem resposta, o MSW passa ao handler de `setup`: só a releitura fica retida.
    server.use(
      http.get(`${API}/rooms/room-1`, async () => {
        await rereadHeld
      }),
    )
    await user.click(button)
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: kinds[kind].button })).not.toBeInTheDocument()
    })
    expect(screen.queryByRole('button', { name: 'Leave room' })).not.toBeInTheDocument()
    expect(screen.queryByText("Your last choice wasn't saved.")).not.toBeInTheDocument()
    releaseReread()
    expect(await screen.findByText("Your last choice wasn't saved.")).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('button', { name: 'Leave room' })).not.toBeInTheDocument()
  })
  it('404 followed by an Expired reread shows the expired progress with the choice-not-saved notice, no alert', async () => {
    setup(kind, 404, true)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: kinds[kind].button }))
    expect(await screen.findByText('Time is up. Your progress is shown below.')).toBeInTheDocument()
    expect(screen.getByText("Your last choice wasn't saved.")).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText('This room has been closed.')).not.toBeInTheDocument()
  })
  it.each([409, 500])('%i keeps the generic alert and the actions', async (status) => {
    setup(kind, status)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: kinds[kind].button }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText("Your last choice wasn't saved.")).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: kinds[kind].button })).toBeInTheDocument()
  })
})
