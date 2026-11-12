import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
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

/** A releitura continua devolvendo `Active`: o job de prazo ainda não rodou (#462). */
function setup(kind: keyof typeof kinds, actionStatus: number) {
  const { path, game, routesGame } = kinds[kind]
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
        state: 'Active',
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
  it('404 shows the neutral notice and hides the actions even when the reread is still Active', async () => {
    setup(kind, 404)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: kinds[kind].button }))
    expect(await screen.findByText('This encounter is not available.')).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: kinds[kind].button })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Leave room' })).not.toBeInTheDocument()
  })
  it('500 keeps the generic alert and the actions', async () => {
    setup(kind, 500)
    useAntiforgeryEndpoint()
    const { user } = await renderApp('/rooms/room-1', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: kinds[kind].button }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText('This encounter is not available.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: kinds[kind].button })).toBeInTheDocument()
  })
})
