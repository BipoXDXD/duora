import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'
import { isSelfieDocument, loadSelfieDocument, reloadCurrentDocument } from './selfie-document'

// Os testes de jornada simulam o documento carregado em /account/selfie (o que a borda exige para a câmera).
vi.mock('./selfie-document', () => ({
  isSelfieDocument: vi.fn(() => true),
  loadSelfieDocument: vi.fn(),
  reloadCurrentDocument: vi.fn(),
}))

const pendingAccount = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'PendingEligibility',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}

describe('documento próprio da selfie', () => {
  afterEach(() => {
    vi.mocked(isSelfieDocument).mockReturnValue(true)
  })

  it('sem o documento de /account/selfie recarrega a página nele em vez de abrir a jornada', async () => {
    vi.mocked(isSelfieDocument).mockReturnValue(false)
    let statusReads = 0
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(pendingAccount))),
      http.get(`${API}/me/selfie-verifications/current`, () => {
        statusReads++
        return HttpResponse.json({
          id: null,
          state: 'NotStarted',
          nextStep: 'Start',
          feedbackCode: null,
          expiresAt: null,
        })
      }),
    )

    await renderApp('/account/selfie')

    await vi.waitFor(() => {
      expect(loadSelfieDocument).toHaveBeenCalledTimes(1)
    }, ROUTE_CHUNK_TIMEOUT_MS)
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
    expect(statusReads).toBe(0)
  })

  it('ao sair para outra rota recarrega o destino para devolver a política comum', async () => {
    vi.mocked(reloadCurrentDocument).mockClear()
    vi.mocked(loadSelfieDocument).mockClear()
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(pendingAccount))),
      http.get(`${API}/me/selfie-verifications/current`, () =>
        HttpResponse.json({ id: null, state: 'NotStarted', nextStep: 'Start', feedbackCode: null, expiresAt: null }),
      ),
    )
    const { user } = await renderApp('/account/selfie')
    await screen.findByRole('button', { name: 'Iniciar captura' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS })
    expect(reloadCurrentDocument).not.toHaveBeenCalled()
    expect(loadSelfieDocument).not.toHaveBeenCalled()

    await user.click(screen.getByRole('link', { name: 'Voltar à conta' }))

    await vi.waitFor(() => {
      expect(reloadCurrentDocument).toHaveBeenCalledTimes(1)
    })
  })
})

describe('selfie sem idade', () => {
  it('explica presença e qualidade sem solicitar documento', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(
          withSessionContext({
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'test@example.com',
            status: 'PendingEligibility',
            emailConfirmed: true,
            mfaEnabled: false,
            roles: ['participant'],
          }),
        ),
      ),
      http.get(`${API}/me/selfie-verifications/current`, () =>
        HttpResponse.json({ id: null, state: 'NotStarted', nextStep: 'Start', feedbackCode: null, expiresAt: null }),
      ),
    )
    await renderApp('/account/selfie')
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
      HttpResponse.json(
        withSessionContext({
          id: firstId,
          email: 'test@example.com',
          status: 'PendingEligibility',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
        }),
      ),
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
  await renderApp('/account/selfie')
  await user.click(await screen.findByRole('button', { name: 'Abrir câmera' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }))
  expect(await screen.findByText(`Camera session: ${firstId}`)).toBeInTheDocument()
  expired = true
  await user.click(await screen.findByRole('button', { name: 'Iniciar captura' }, { timeout: 5000 }))
  await user.click(await screen.findByRole('button', { name: 'Abrir câmera' }))
  expect(await screen.findByText(`Camera session: ${secondId}`)).toBeInTheDocument()
  expect(screen.queryByText(`Camera session: ${firstId}`)).not.toBeInTheDocument()
})
