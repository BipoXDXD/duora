import { act, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { API, server } from '@/test/msw'
import { withSessionContext } from '@/test/private-context'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'

vi.mock('./selfie-document', () => ({
  isSelfieDocument: () => true,
  loadSelfieDocument: vi.fn(),
  reloadCurrentDocument: vi.fn(),
}))

const SERVER_NOW = Date.parse('2026-10-30T15:28:00Z')
const LOCKED_UNTIL = '2026-10-30T15:30:00Z'
const MINUTE = 60_000
const account = withSessionContext({
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'synthetic@example.test',
  status: 'PendingEligibility',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
})

function setup(clientOffset: number) {
  vi.useFakeTimers({
    shouldAdvanceTime: true,
    toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'],
  })
  vi.setSystemTime(SERVER_NOW + clientOffset)
  let serverNow = SERVER_NOW
  let reads = 0
  let exhausted = false
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(account)),
    http.get(`${API}/me/selfie-verifications/current`, () => {
      reads++
      return HttpResponse.json({
        id: null,
        state: 'NotStarted',
        nextStep: 'Start',
        feedbackCode: null,
        expiresAt: null,
        attempts: exhausted
          ? { state: 'Exhausted', remaining: 0, lastCycle: true, lockedUntil: null }
          : serverNow >= SERVER_NOW + 2 * MINUTE
            ? { state: 'Available', remaining: 5, lastCycle: true, lockedUntil: null }
            : { state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: LOCKED_UNTIL },
      })
    }),
  )
  return {
    get reads() {
      return reads
    },
    exhaust() {
      exhausted = true
    },
    async advance(ms: number) {
      serverNow += ms
      await act(() => vi.advanceTimersByTimeAsync(ms))
    },
  }
}

async function open() {
  const view = await renderApp('/account/selfie')
  await screen.findByText(/Você pode tentar de novo a partir de/, {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })
  return view
}

afterEach(() => {
  vi.useRealTimers()
})

describe('revalidação da pausa pelo estado do servidor', () => {
  it.each([
    ['adiantado', 2 * 60 * MINUTE],
    ['atrasado', -2 * 60 * MINUTE],
    ['sincronizado', 0],
  ] as const)('com relógio %s mantém a pausa e reabre somente após confirmação', async (_, offset) => {
    const clock = setup(offset)
    const view = await open()
    expect(clock.reads).toBe(1)

    await clock.advance(59_000)
    expect(clock.reads).toBe(1)
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()

    await clock.advance(1_000)
    await vi.waitFor(() => {
      expect(clock.reads).toBe(2)
    })
    // A resposta é idêntica: o servidor ainda não terminou a pausa.
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    expect(screen.getByText(/Você pode tentar de novo a partir de/)).toBeInTheDocument()

    await clock.advance(59_000)
    expect(clock.reads).toBe(2)
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    await clock.advance(1_000)
    expect(await screen.findByRole('button', { name: 'Iniciar captura' })).toBeEnabled()
    expect(screen.queryByText(/Você pode tentar de novo a partir de/)).not.toBeInTheDocument()
    expect(clock.reads).toBe(3)

    await clock.advance(5 * MINUTE)
    expect(clock.reads).toBe(3)
    view.unmount()
    view.queryClient.clear()
  })

  it('cancela a revalidação quando a página é desmontada', async () => {
    const clock = setup(0)
    const view = await open()
    view.unmount()

    await clock.advance(5 * MINUTE)

    expect(clock.reads).toBe(1)
    view.queryClient.clear()
  })

  it('não acumula leituras lentas e aborta a leitura pendente ao desmontar', async () => {
    const clock = setup(0)
    const view = await open()
    let pendingReads = 0
    let signal: AbortSignal | undefined
    let finish: (() => void) | undefined
    server.use(
      http.get(`${API}/me/selfie-verifications/current`, async ({ request }) => {
        pendingReads++
        signal = request.signal
        await new Promise<void>((resolve) => {
          finish = resolve
        })
        return HttpResponse.json({
          id: null,
          state: 'NotStarted',
          nextStep: 'Start',
          feedbackCode: null,
          expiresAt: null,
          attempts: { state: 'Available', remaining: 5, lastCycle: true, lockedUntil: null },
        })
      }),
    )
    try {
      await clock.advance(MINUTE)
      await vi.waitFor(() => {
        expect(pendingReads).toBe(1)
      })
      await clock.advance(5 * MINUTE)
      expect(pendingReads).toBe(1)
      expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()

      view.unmount()
      expect(signal?.aborted).toBe(true)
      finish?.()
      await clock.advance(5 * MINUTE)
      expect(pendingReads).toBe(1)
      expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    } finally {
      finish?.()
      view.unmount()
      view.queryClient.clear()
    }
  })

  it('trocar o relógio durante a pausa não antecipa nem adia a revalidação', async () => {
    const clock = setup(0)
    const view = await open()
    vi.setSystemTime(SERVER_NOW + 24 * 60 * MINUTE)

    await clock.advance(MINUTE)
    await vi.waitFor(() => {
      expect(clock.reads).toBe(2)
    })
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    vi.setSystemTime(SERVER_NOW - 24 * 60 * MINUTE)
    await clock.advance(MINUTE)
    expect(await screen.findByRole('button', { name: 'Iniciar captura' })).toBeEnabled()
    expect(clock.reads).toBe(3)
    view.unmount()
    view.queryClient.clear()
  })

  it('interrompe a revalidação ao receber Exhausted e mantém a captura bloqueada', async () => {
    const clock = setup(0)
    const view = await open()
    clock.exhaust()

    await clock.advance(MINUTE)
    expect(await screen.findByText(/As tentativas acabaram e a conta foi bloqueada/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    expect(clock.reads).toBe(2)
    await clock.advance(5 * MINUTE)
    expect(clock.reads).toBe(2)
    view.unmount()
    view.queryClient.clear()
  })
})
