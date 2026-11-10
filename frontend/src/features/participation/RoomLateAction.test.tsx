import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'
import type { Locale } from '@/shared/i18n/locale'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
const deadline = '2026-10-22T18:00:00Z'
const constellation = {
  deadline,
  title: 'Escolhas',
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
const routes = {
  experienceId: 'unlikely-routes',
  rulesVersion: 1,
  contentVersion: 1,
  schemaVersion: 1,
  resourceVersion: '4',
  status: 'Active',
  deadline,
  stage: 'Introduction',
  title: 'Rotas Improváveis',
  objective: 'Criem um passeio juntos.',
  example: 'Uma praça e um café.',
  myClues: [],
  currentStop: null,
  isMyTurn: false,
  route: [],
  revisionsRemaining: 1,
  awaitingPartner: false,
  availableActions: ['acknowledge'],
}
const NOT_SAVED: Readonly<Record<Locale, string>> = {
  'pt-BR': 'Sua última escolha não foi registrada.',
  'en-US': "Your last choice wasn't saved.",
}

function notFound(): Response {
  return HttpResponse.json(
    { type: 'urn:duora:problem:not-found', title: 'Not Found', status: 404, traceId: '00-late' },
    { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

/** A sala responde com o estado do momento; a ação depois do prazo recebe 404 e a sala passa a `Expired`. */
function useLateActionRoom(kind: 'constellation' | 'routes', reply: () => Response = notFound) {
  const snapshot = { state: 'Active' }
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/rooms/late`, () =>
      HttpResponse.json({
        roomId: 'late',
        state: snapshot.state,
        version: '4',
        communicationLanguage: 'pt',
        communicationPolicyVersion: 1,
        displayLocale: 'pt-BR',
        invitationExpiresAt: deadline,
        game: kind === 'constellation' ? constellation : null,
        routesGame: kind === 'routes' ? routes : null,
        audio: [],
      }),
    ),
    http.post(`${API}/rooms/late/${kind === 'constellation' ? 'actions' : 'routes-actions'}`, () => {
      const response = reply()
      if (response.status === 404) {
        snapshot.state = 'Expired'
      }
      return response
    }),
  )
  useAntiforgeryEndpoint()
  return snapshot
}

// Só a primeira tela espera mais que o padrão de 1 s: com a máquina carregada, ela ainda pode estar em "Carregando suas
// preferências…". As asserções da corrida mantêm o timeout padrão; o teste ganha folga para essa espera caber nele.
const FIRST_SCREEN = { timeout: 5_000 }
const SLOW_START = { timeout: 15_000 }

const actionButton = { constellation: 'Parque', routes: 'Entendi' } as const
const actionButtonInEnglish = { constellation: 'Parque', routes: 'Got it' } as const

describe.each(['constellation', 'routes'] as const)('late %s action', (kind) => {
  // O 404 vale para sala Expired por qualquer motivo: o aviso diz só o que aconteceu com a escolha.
  it('shows a neutral status instead of the generic alert', SLOW_START, async () => {
    useLateActionRoom(kind)
    const { user } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: actionButton[kind] }, FIRST_SCREEN))

    expect(await screen.findByText(NOT_SAVED['pt-BR'])).toHaveAttribute('role', 'status')
    expect(screen.getByText('O prazo terminou. Veja abaixo o progresso realizado.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  // Os controles do jogo somem com a sala Expired; o foco não pode cair no body (WCAG 2.4.3).
  it('moves focus to the notice when the game controls go away', SLOW_START, async () => {
    useLateActionRoom(kind)
    const { user } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: actionButton[kind] }, FIRST_SCREEN))

    const notice = await screen.findByText(NOT_SAVED['pt-BR'])
    await waitFor(() => {
      expect(notice).toHaveFocus()
    })
  })

  it('is translated to English', SLOW_START, async () => {
    useLateActionRoom(kind)
    const { user } = await renderApp('/rooms/late', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: actionButtonInEnglish[kind] }, FIRST_SCREEN))

    expect(await screen.findByText(NOT_SAVED['en-US'])).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('late action notice', () => {
  it('goes away when the room state changes again', SLOW_START, async () => {
    const snapshot = useLateActionRoom('constellation')
    const { user, queryClient } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: 'Parque' }, FIRST_SCREEN))
    expect(await screen.findByText(NOT_SAVED['pt-BR'])).toBeInTheDocument()

    snapshot.state = 'Completed'
    await queryClient.refetchQueries()

    expect(await screen.findByText('Experiência concluída.')).toBeInTheDocument()
    expect(screen.queryByText(NOT_SAVED['pt-BR'])).not.toBeInTheDocument()
  })

  // A falha perde a validade na primeira mudança de estado depois de aparecer, mesmo que a sala volte ao estado dela.
  it('stays away when the room returns to the state of the failure', SLOW_START, async () => {
    const snapshot = useLateActionRoom('constellation')
    const { user, queryClient } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: 'Parque' }, FIRST_SCREEN))
    expect(await screen.findByText(NOT_SAVED['pt-BR'])).toBeInTheDocument()

    snapshot.state = 'Completed'
    await queryClient.refetchQueries()
    await screen.findByText('Experiência concluída.')
    snapshot.state = 'Expired'
    await queryClient.refetchQueries()

    expect(await screen.findByText('O prazo terminou. Veja abaixo o progresso realizado.')).toBeInTheDocument()
    expect(screen.queryByText(NOT_SAVED['pt-BR'])).not.toBeInTheDocument()
  })

  it('keeps the error alert for failures other than 404', SLOW_START, async () => {
    useLateActionRoom('constellation', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:internal', title: 'Internal', status: 500, traceId: '00-boom' },
        { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    const { user } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: 'Parque' }, FIRST_SCREEN))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText(NOT_SAVED['pt-BR'])).not.toBeInTheDocument()
  })

  it('drops the error alert when the room state changes', SLOW_START, async () => {
    const snapshot = useLateActionRoom('constellation', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:internal', title: 'Internal', status: 500, traceId: '00-boom' },
        { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    const { user, queryClient } = await renderApp('/rooms/late')
    await user.click(await screen.findByRole('button', { name: 'Parque' }, FIRST_SCREEN))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    snapshot.state = 'Expired'
    await queryClient.refetchQueries()

    expect(await screen.findByText('O prazo terminou. Veja abaixo o progresso realizado.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
