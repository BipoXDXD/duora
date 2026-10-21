import { act, screen, waitFor } from '@testing-library/react'
import type { UserEvent } from '@testing-library/user-event'
import type { FaceLivenessDetectorCore } from '@aws-amplify/ui-react-liveness'
import { useEffect, type ComponentProps } from 'react'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

type DetectorProps = ComponentProps<typeof FaceLivenessDetectorCore>
const sdk = vi.hoisted(() => ({ current: null as DetectorProps | null }))

// Keep SelfieCapture and the journey real; only replace the external capture SDK.
// The double never initializes media devices, credentials or a provider connection.
vi.mock('@aws-amplify/ui-react-liveness', () => ({
  FaceLivenessDetectorCore: function SyntheticDetector(props: DetectorProps) {
    useEffect(() => {
      sdk.current = props
      return () => {
        sdk.current = null
      }
    }, [props])
    return null
  },
}))

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const ATTEMPT_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
const SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c8'
const PATH = `${API}/me/selfie-verifications`
const CANARY = '<img src=x onerror="window.CANARY=1">CANARY unexpected server text'
let removeReceipt: (() => void) | undefined

beforeEach(() => {
  sdk.current = null
})

afterEach(() => {
  removeReceipt?.()
  removeReceipt = undefined
})

function installCaptureBackend() {
  const calls = { captures: 0, completions: [] as { id: string; body: unknown; csrf: string | null }[] }
  const delivered: number[] = []
  const receipt = ({ request, response }: { request: Request; response: Response }) => {
    if (request.method === 'POST' && new URL(request.url).pathname.endsWith('/complete')) {
      delivered.push(response.status)
    }
  }
  server.events.on('response:mocked', receipt)
  removeReceipt = () => {
    server.events.removeListener('response:mocked', receipt)
  }
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json({
        id: USER_ID,
        email: 'synthetic@example.com',
        status: 'PendingEligibility',
        emailConfirmed: true,
        mfaEnabled: false,
        roles: ['participant'],
      }),
    ),
    http.get(`${PATH}/current`, () =>
      HttpResponse.json({
        id: ATTEMPT_ID,
        state: 'Capturing',
        nextStep: 'Capture',
        feedbackCode: null,
        expiresAt: null,
      }),
    ),
    http.post(`${PATH}/${ATTEMPT_ID}/capture`, () => {
      calls.captures++
      return HttpResponse.json({
        sessionId: SESSION_ID,
        region: 'us-east-1',
        credentials: {
          accessKeyId: 'synthetic',
          secretAccessKey: 'synthetic',
          sessionToken: 'synthetic',
          expiration: '2099-01-01T00:00:00Z',
        },
      })
    }),
    http.post(`${PATH}/:id/complete`, async ({ params, request }) => {
      calls.completions.push({
        id: String(params.id),
        body: await request.json(),
        csrf: request.headers.get('X-XSRF-TOKEN'),
      })
      return new HttpResponse(
        JSON.stringify({
          type: 'urn:duora:problem:not-found',
          status: 404,
          code: 'NOT_FOUND',
          title: CANARY,
          detail: CANARY,
          errors: { body: [CANARY] },
          traceId: 'synthetic-callback',
        }),
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      )
    }),
  )
  return { calls, delivered }
}

async function openUsingKeyboard(user: UserEvent, name: string): Promise<DetectorProps> {
  const button = await screen.findByRole('button', { name })
  button.focus()
  expect(button).toHaveFocus()
  await user.keyboard('{Enter}')
  return waitFor(() => {
    if (sdk.current === null) throw new Error('The real wrapper has not mounted the SDK double')
    return sdk.current
  })
}

for (const copy of [
  {
    locale: 'pt-BR',
    title: 'Confirmar presença',
    open: 'Abrir câmera',
    retry: 'Não foi possível concluir. Ajuste a iluminação e o enquadramento antes de tentar novamente.',
    done: 'Presença confirmada.',
    back: 'Voltar à conta',
  },
  {
    locale: 'en-US',
    title: 'Confirm presence',
    open: 'Open camera',
    retry: 'The check could not be completed. Adjust lighting and framing before trying again.',
    done: 'Presence confirmed.',
    back: 'Back to account',
  },
] as const) {
  describe(`synthetic selfie callbacks / ${copy.locale}`, () => {
    for (const event of ['complete', 'cancel', 'error'] as const) {
      it(`${event} clears capture, keeps local feedback and allows keyboard reopening`, async () => {
        const csrf = useAntiforgeryEndpoint()
        const { calls, delivered } = installCaptureBackend()
        const { user } = await renderApp('/account/selfie', undefined, copy.locale)
        const detector = await openUsingKeyboard(user, copy.open)

        if (event === 'complete') {
          // Await the actual journey callback, including HTTP rejection handling.
          await act(async () => {
            await expect(detector.onAnalysisComplete()).resolves.toBeUndefined()
          })
          expect(csrf.latestToken()).not.toBeNull()
          expect(calls.completions).toEqual([{ id: ATTEMPT_ID, body: {}, csrf: csrf.latestToken() }])
          expect(delivered).toEqual([404])
        } else {
          act(() => {
            if (event === 'cancel') {
              if (!detector.onUserCancel) throw new Error('Missing SDK cancellation callback')
              detector.onUserCancel()
            } else {
              if (!detector.onError) throw new Error('Missing SDK error callback')
              detector.onError({ state: 'SERVER_ERROR', error: new Error(CANARY) })
            }
          })
          expect(calls.completions).toEqual([])
          expect(delivered).toEqual([])
        }

        expect(await screen.findByRole('status')).toHaveTextContent(copy.retry)
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
        expect(screen.queryByText(copy.done)).not.toBeInTheDocument()
        expect(screen.getByRole('heading', { name: copy.title, level: 1 })).toBeInTheDocument()
        expect(screen.getByRole('link', { name: copy.back })).toHaveAttribute('href', '/account')
        expect(document.documentElement).toHaveAttribute('lang', copy.locale)
        expect(document.documentElement.textContent).not.toContain('CANARY')
        expect(document.documentElement.outerHTML).not.toContain('CANARY')
        expect(Object.hasOwn(window, 'CANARY')).toBe(false)
        expect(sdk.current).toBeNull()
        expect(screen.getByRole('button', { name: copy.open })).toBeEnabled()
        expect(calls.captures).toBe(1)

        await openUsingKeyboard(user, copy.open)
        expect(calls.captures).toBe(2)
        expect(screen.queryByText(copy.retry)).not.toBeInTheDocument()
        expect(calls.completions).toHaveLength(event === 'complete' ? 1 : 0)
      })
    }
  })
}
