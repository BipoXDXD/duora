import { withSessionContext } from '@/test/private-context'
import { act, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { API, server } from '@/test/msw'
import { renderApp } from '@/test/render'
import type { Locale } from '@/shared/i18n/locale'

const deadline = '2026-10-22T18:00:00Z'
const card = { cardId: 'second', position: 2, prompt: 'Next card', options: [{ optionId: 'read', label: 'Read' }] }
const revealed = [
  { cardId: 'first', prompt: 'First card', myOptionLabel: 'Music', partnerOptionLabel: 'Books', choseTheSame: false },
]
function setup(kind: string, state = 'Active') {
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
    http.get(`${API}/rooms/deadline`, () =>
      HttpResponse.json({
        roomId: 'deadline',
        state,
        version: '4',
        communicationLanguage: 'en',
        communicationPolicyVersion: 1,
        displayLocale: 'en-US',
        invitationExpiresAt: deadline,
        game:
          kind === 'constellation'
            ? {
                title: 'Choices',
                objective: 'Choose together',
                deadline,
                currentCard: state === 'Active' ? card : null,
                awaitingPartner: false,
                revealed,
                passed: [],
                availableActions: state === 'Active' ? ['choose-option'] : [],
              }
            : null,
        routesGame:
          kind === 'routes'
            ? {
                experienceId: 'unlikely-routes',
                rulesVersion: 1,
                contentVersion: 1,
                schemaVersion: 1,
                resourceVersion: '4',
                status: state,
                deadline,
                stage: state === 'Active' ? 'Choices' : 'Conclusion',
                title: 'Routes',
                objective: 'Plan together',
                example: 'An island',
                myClues: ['Your private clue'],
                currentStop:
                  state === 'Active'
                    ? { stepId: 'second', prompt: 'Next stop', options: [{ optionId: 'park', label: 'Park' }] }
                    : null,
                isMyTurn: state === 'Active',
                route: [{ stepId: 'first', prompt: 'Start', optionLabel: 'Garden' }],
                revisionsRemaining: 1,
                awaitingPartner: false,
                availableActions: state === 'Active' ? ['choose-option'] : [],
              }
            : null,
        audio: [],
      }),
    ),
  )
}
afterEach(() => {
  vi.useRealTimers()
})
const locales: Locale[] = ['pt-BR', 'en-US']
describe.each(['constellation', 'routes'])('%s deadlines', (kind) => {
  it.each(locales)('warns once at each threshold without moving focus (%s)', async (locale) => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] })
    vi.setSystemTime(new Date('2026-10-22T17:57:59Z'))
    setup(kind)
    await renderApp('/rooms/deadline', undefined, locale)
    await screen.findByRole('heading', { name: kind === 'constellation' ? 'Choices' : 'Routes' })
    const exit = screen.getByRole('button', { name: locale === 'pt-BR' ? 'Sair da sala' : 'Leave room' })
    exit.focus()
    const status = screen.getByRole('status', { name: locale === 'pt-BR' ? 'Avisos de prazo' : 'Time warnings' })
    expect(status).toBeEmptyDOMElement()
    const notices: string[] = []
    const observer = new MutationObserver(() => {
      notices.push(status.textContent)
    })
    observer.observe(status, { childList: true, subtree: true, characterData: true })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000)
    })
    expect(status).toHaveTextContent(locale === 'pt-BR' ? 'Restam 2 minutos.' : '2 minutes remaining.')
    await act(async () => {
      await vi.advanceTimersByTimeAsync(89_000)
    })
    expect(notices).toHaveLength(1)
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000)
    })
    expect(status).toHaveTextContent(locale === 'pt-BR' ? 'Restam 30 segundos.' : '30 seconds remaining.')
    await act(async () => {
      await vi.advanceTimersByTimeAsync(20_000)
    })
    expect(notices).toHaveLength(2)
    expect(exit).toHaveFocus()
    observer.disconnect()
  })
  it.each(locales)('keeps expired progress read-only (%s)', async (locale) => {
    setup(kind, 'Expired')
    await renderApp('/rooms/deadline', undefined, locale)
    expect(
      await screen.findByText(
        locale === 'pt-BR'
          ? 'O prazo terminou. Veja abaixo o progresso realizado.'
          : 'Time is up. Your progress is shown below.',
      ),
    ).toBeInTheDocument()
    expect(screen.getByText(kind === 'constellation' ? 'First card' : 'Garden')).toBeInTheDocument()
    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Read|Choose|Escolher/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('status', { name: /Avisos de prazo|Time warnings/ })).not.toBeInTheDocument()
  })
})
it('entering with less than 30 seconds shows only the latest warning', async () => {
  vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] })
  vi.setSystemTime(new Date('2026-10-22T17:59:40Z'))
  setup('constellation')
  await renderApp('/rooms/deadline', undefined, 'en-US')
  expect(await screen.findByText('30 seconds remaining.')).toBeInTheDocument()
  expect(screen.queryByText('2 minutes remaining.')).not.toBeInTheDocument()
})

it('resuming a background tab announces only the latest threshold', async () => {
  vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] })
  vi.setSystemTime(new Date('2026-10-22T17:57:00Z'))
  setup('constellation')
  await renderApp('/rooms/deadline', undefined, 'en-US')
  const status = await screen.findByRole('status', { name: 'Time warnings' })
  expect(status).toBeEmptyDOMElement()
  act(() => {
    vi.setSystemTime(new Date('2026-10-22T17:59:40Z'))
    document.dispatchEvent(new Event('visibilitychange'))
  })
  expect(status).toHaveTextContent('30 seconds remaining.')
  expect(screen.queryByText('2 minutes remaining.')).not.toBeInTheDocument()
})
