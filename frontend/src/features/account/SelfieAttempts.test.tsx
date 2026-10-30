import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'
import type { Locale } from '@/shared/i18n/locale'

// Limite de reprovações da selfie (issue #300): a tela só lê o estado que o servidor decide.
vi.mock('./selfie-document', () => ({
  isSelfieDocument: vi.fn(() => true),
  loadSelfieDocument: vi.fn(),
  reloadCurrentDocument: vi.fn(),
}))

const account = withSessionContext({
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'PendingEligibility',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
  preferredTimeZone: 'America/Sao_Paulo',
})

const LOCKED_UNTIL = '2026-10-30T15:30:00Z'

type Attempts = Readonly<{ state: string; remaining: number; lastCycle: boolean; lockedUntil: string | null }>

function serve(attempts: Attempts): void {
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(account)),
    http.get(`${API}/me/selfie-verifications/current`, () =>
      HttpResponse.json({
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7',
        state: 'RecaptureRequired',
        nextStep: 'Recapture',
        feedbackCode: 'CAPTURE_QUALITY',
        expiresAt: '2026-10-30T14:00:00Z',
        attempts,
      }),
    ),
  )
}

async function open(locale: Locale = 'pt-BR'): Promise<void> {
  await renderApp('/account/selfie', undefined, locale)
  await screen.findByRole('heading', { level: 1 }, { timeout: ROUTE_CHUNK_TIMEOUT_MS })
}

describe('tentativas da selfie', () => {
  it('no primeiro ciclo avisa quantas tentativas restam antes da pausa de 1 hora', async () => {
    serve({ state: 'Available', remaining: 2, lastCycle: false, lockedUntil: null })

    await open()

    expect(await screen.findByText('Restam 2 tentativas antes de uma pausa de 1 hora.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Iniciar captura' })).toBeInTheDocument()
  })

  it('com uma tentativa usa o singular', async () => {
    serve({ state: 'Available', remaining: 1, lastCycle: false, lockedUntil: null })

    await open()

    expect(await screen.findByText('Resta 1 tentativa antes de uma pausa de 1 hora.')).toBeInTheDocument()
  })

  it('sem reprovação no primeiro ciclo não mostra contagem', async () => {
    serve({ state: 'Available', remaining: 5, lastCycle: false, lockedUntil: null })

    await open()

    await screen.findByRole('button', { name: 'Iniciar captura' })
    expect(screen.queryByText(/antes de uma pausa/)).not.toBeInTheDocument()
  })

  it('no segundo ciclo avisa que a conta será bloqueada', async () => {
    serve({ state: 'Available', remaining: 5, lastCycle: true, lockedUntil: null })

    await open()

    expect(
      await screen.findByText('Restam 5 tentativas. Se nenhuma for aprovada, a conta será bloqueada.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Iniciar captura' })).toBeInTheDocument()
  })

  it('na pausa mostra quando a selfie volta, no fuso da conta, e tira o botão de captura', async () => {
    serve({ state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: LOCKED_UNTIL })

    await open()

    const notice = await screen.findByText(/Você pode tentar de novo a partir de/)
    expect(notice).toHaveTextContent('12:30')
    expect(notice.querySelector('time')).toHaveAttribute('dateTime', LOCKED_UNTIL)
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
  })

  it('esgotadas as tentativas diz que a conta foi bloqueada e não oferece nova captura', async () => {
    serve({ state: 'Exhausted', remaining: 0, lastCycle: true, lockedUntil: null })

    await open()

    expect(
      await screen.findByText('As tentativas acabaram e a conta foi bloqueada. Só uma revisão humana pode liberá-la.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
  })

  it('em inglês mostra a pausa com o mesmo horário', async () => {
    serve({ state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: LOCKED_UNTIL })

    await open('en-US')

    expect(await screen.findByText(/You can try again from/)).toHaveTextContent('12:30')
  })

  it('em inglês avisa o bloqueio da conta no segundo ciclo', async () => {
    serve({ state: 'Available', remaining: 1, lastCycle: true, lockedUntil: null })

    await open('en-US')

    expect(
      await screen.findByText('1 attempt left. If none is approved, the account will be blocked.'),
    ).toBeInTheDocument()
  })

  it('um 429 do servidor relê o estado e mostra a pausa sem o aviso genérico', async () => {
    useAntiforgeryEndpoint()
    let locked = false
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(account)),
      http.get(`${API}/me/selfie-verifications/current`, () =>
        HttpResponse.json({
          id: null,
          state: 'NotStarted',
          nextStep: 'Start',
          feedbackCode: null,
          expiresAt: null,
          attempts: locked
            ? { state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: LOCKED_UNTIL }
            : { state: 'Available', remaining: 1, lastCycle: false, lockedUntil: null },
        }),
      ),
      http.post(`${API}/me/selfie-verifications`, () => {
        locked = true
        return HttpResponse.json(
          { status: 429, title: 'Too many rejected selfies.', code: 'SELFIE_ATTEMPTS_LOCKED' },
          { status: 429, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '3600' } },
        )
      }),
    )
    const { user } = await renderApp('/account/selfie')

    await user.click(
      await screen.findByRole('button', { name: 'Iniciar captura' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByText(/Você pode tentar de novo a partir de/)).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
  })

  it('quando a pausa termina, relê o estado sozinha e devolve a captura', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ['setTimeout', 'clearTimeout', 'Date'] })
    try {
      const until = new Date(Date.now() + 60_000).toISOString()
      let reads = 0
      server.use(
        http.get(`${API}/me`, () => HttpResponse.json(account)),
        http.get(`${API}/me/selfie-verifications/current`, () => {
          reads++
          const reopened = Date.now() >= Date.parse(until)
          return HttpResponse.json({
            id: null,
            state: 'NotStarted',
            nextStep: 'Start',
            feedbackCode: null,
            expiresAt: null,
            attempts: reopened
              ? { state: 'Available', remaining: 5, lastCycle: true, lockedUntil: null }
              : { state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: until },
          })
        }),
      )
      await open()
      await screen.findByText(/Você pode tentar de novo a partir de/)
      const readsDuringPause = reads

      await vi.advanceTimersByTimeAsync(59_000)
      const readsBeforeTheEnd = reads
      await vi.advanceTimersByTimeAsync(1_500)

      expect(readsBeforeTheEnd).toBe(readsDuringPause)
      expect(await screen.findByRole('button', { name: 'Iniciar captura' })).toBeInTheDocument()
      expect(screen.queryByText(/Você pode tentar de novo a partir de/)).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})
