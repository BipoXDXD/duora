import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'

describe('selfie sem idade', () => {
  it('explica presença e qualidade sem solicitar documento', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json({
          id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
          email: 'test@example.com',
          status: 'PendingEligibility',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
        }),
      ),
      http.get(`${API}/me/selfie-verifications/current`, () =>
        HttpResponse.json({ id: null, state: 'NotStarted', nextStep: 'Start', feedbackCode: null, expiresAt: null }),
      ),
    )
    await renderApp('/conta/selfie')
    expect(
      await screen.findByRole('heading', { name: 'Confirmar presença', level: 1 }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Iniciar captura' })).toBeInTheDocument()
    expect(screen.getByText(/Não estimamos sua idade/)).toBeInTheDocument()
  })
})

vi.mock('./SelfieCapture', () => ({
  default: ({ session }: { session: { sessionId: string } }) => <p>Camera session: {session.sessionId}</p>,
}))

it('após expirar solicita credenciais novas para a nova tentativa', async () => {
  useAntiforgeryEndpoint()
  const firstId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
  const secondId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
  let id = firstId
  let expired = false
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json({
        id: firstId,
        email: 'test@example.com',
        status: 'PendingEligibility',
        emailConfirmed: true,
        mfaEnabled: false,
        roles: ['participant'],
      }),
    ),
    http.get(`${API}/me/selfie-verifications/current`, () =>
      HttpResponse.json({
        id,
        state: expired ? 'Expired' : 'Capturing',
        nextStep: expired ? 'Recapture' : 'Capture',
        feedbackCode: null,
        expiresAt: '2026-10-19T15:00:00Z',
      }),
    ),
    http.post(`${API}/me/selfie-verifications`, () => {
      id = secondId
      expired = false
      return HttpResponse.json(
        { id, state: 'Capturing', nextStep: 'Capture', feedbackCode: null, expiresAt: null },
        { status: 202 },
      )
    }),
    http.post(`${API}/me/selfie-verifications/:id/capture`, () =>
      HttpResponse.json({
        sessionId: id,
        region: 'us-east-1',
        credentials: {
          accessKeyId: 'synthetic',
          secretAccessKey: 'synthetic',
          sessionToken: 'synthetic',
          expiration: '2026-10-19T15:00:00Z',
        },
      }),
    ),
  )
  const user = userEvent.setup()
  await renderApp('/conta/selfie')
  await user.click(await screen.findByRole('button', { name: 'Abrir câmera' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }))
  expect(await screen.findByText(`Camera session: ${firstId}`)).toBeInTheDocument()
  expired = true
  await user.click(await screen.findByRole('button', { name: 'Iniciar captura' }, { timeout: 5000 }))
  await user.click(await screen.findByRole('button', { name: 'Abrir câmera' }))
  expect(await screen.findByText(`Camera session: ${secondId}`)).toBeInTheDocument()
  expect(screen.queryByText(`Camera session: ${firstId}`)).not.toBeInTheDocument()
})
