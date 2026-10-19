import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'
import { safeFlowUrl } from './age-verification-api'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@example.com',
  status: 'PendingEligibility',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
function setup(nextStep: string = 'StartVerification', continueUrl: string | null = null) {
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(ME)),
    http.get(`${API}/me/age-verifications/current`, () =>
      HttpResponse.json({ state: 'Pending', nextStep, continueUrl }),
    ),
  )
}

describe('verificação por selfie', () => {
  it('explica captura sem documentos e só abre solicitação por gesto com CSRF', async () => {
    setup()
    const csrf = useAntiforgeryEndpoint()
    const received: unknown[] = []
    server.use(
      http.post(`${API}/me/age-verifications`, async ({ request }) => {
        received.push({ body: await request.json(), csrf: request.headers.get('X-XSRF-TOKEN') })
        return HttpResponse.json({ state: 'Pending', nextStep: 'WaitForReview', continueUrl: null }, { status: 202 })
      }),
    )
    const app = await renderApp('/conta/verificacao-etaria')
    const start = await screen.findByRole(
      'button',
      { name: 'Iniciar verificação por selfie' },
      { timeout: ROUTE_CHUNK_TIMEOUT_MS },
    )
    expect(screen.getByText('Confirme sua idade com uma selfie. Não exigimos documentos.')).toBeInTheDocument()
    expect(received).toEqual([])
    await app.user.click(start)
    expect(await screen.findByText(/Sua verificação está em análise/)).toBeInTheDocument()
    expect(received).toEqual([{ body: {}, csrf: csrf.latestToken() }])
    expect(screen.queryByRole('link', { name: 'Continuar para a selfie' })).not.toBeInTheDocument()
  })

  it('indisponibilidade não aprova a conta nem oferece documentos', async () => {
    setup()
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/me/age-verifications`, () => problem(503, 'AGE_VERIFICATION_UNAVAILABLE')))
    const app = await renderApp('/conta/verificacao-etaria')
    await app.user.click(
      await screen.findByRole(
        'button',
        { name: 'Iniciar verificação por selfie' },
        { timeout: ROUTE_CHUNK_TIMEOUT_MS },
      ),
    )
    expect(await screen.findByRole('alert')).toHaveTextContent('Sua conta permanece pendente.')
    expect(screen.queryByText('Verificação etária concluída.')).not.toBeInTheDocument()
    expect(document.querySelector('input[type="file"]')).toBeNull()
  })

  it('continua somente pelo link HTTPS retornado pela API, sem navegação automática', async () => {
    setup('ContinueWithProvider', 'https://verify.example/flow/opaque')
    await renderApp('/conta/verificacao-etaria')
    const link = await screen.findByRole(
      'link',
      { name: 'Continuar para a selfie' },
      { timeout: ROUTE_CHUNK_TIMEOUT_MS },
    )
    expect(link).toHaveAttribute('href', 'https://verify.example/flow/opaque')
    expect(link).toHaveAttribute('rel', 'noreferrer')
  })

  it.each([
    'javascript:alert(1)',
    'http://verify.example',
    'https://user:secret@verify.example',
    '/relative',
    'not a url',
  ])('recusa URL insegura %s', (value) => {
    expect(safeFlowUrl(value)).toBeNull()
  })
})
