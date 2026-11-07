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

describe('fornecedor instável (PROVIDER_UNAVAILABLE)', () => {
  const attemptId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
  const OUTAGE_NOTICE = /serviço de verificação está instável/
  const EXPIRED_BY_OUTAGE_NOTICE = /tentativa expirou por instabilidade/
  const QUALITY_ADVICE = /Ajuste a iluminação/
  interface Step {
    readonly state: string
    readonly nextStep: string
    readonly feedbackCode: string | null
  }

  function serveSelfie(step: () => Step): void {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(pendingAccount))),
      http.get(`${API}/me/selfie-verifications/current`, () =>
        HttpResponse.json({ id: attemptId, ...step(), expiresAt: '2026-10-19T15:00:00Z' }),
      ),
    )
  }

  async function findNotice(text: RegExp): Promise<HTMLElement> {
    return screen.findByText(text, {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })
  }

  it('na espera avisa da instabilidade, em região anunciada, sem culpar a captura', async () => {
    serveSelfie(() => ({ state: 'Pending', nextStep: 'Wait', feedbackCode: 'PROVIDER_UNAVAILABLE' }))

    await renderApp('/account/selfie')

    const notice = await findNotice(OUTAGE_NOTICE)
    expect(notice.closest('[role="status"]')).not.toBeNull()
    expect(notice).toHaveTextContent('não conta como reprovação')
    expect(screen.getByText('Aguardando verificação…')).toBeInTheDocument()
    expect(screen.queryByText(QUALITY_ADVICE)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Iniciar captura' })).not.toBeInTheDocument()
  })

  it('com a sessão aberta avisa e mantém a câmera disponível', async () => {
    serveSelfie(() => ({ state: 'Capturing', nextStep: 'Capture', feedbackCode: 'PROVIDER_UNAVAILABLE' }))

    await renderApp('/account/selfie')

    expect(await findNotice(OUTAGE_NOTICE)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abrir câmera' })).toBeInTheDocument()
  })

  it('na expiração troca o conselho de luz pela instabilidade e oferece nova captura', async () => {
    serveSelfie(() => ({ state: 'Expired', nextStep: 'Recapture', feedbackCode: 'PROVIDER_UNAVAILABLE' }))

    await renderApp('/account/selfie')

    const notice = await findNotice(EXPIRED_BY_OUTAGE_NOTICE)
    expect(notice.closest('[role="status"]')).not.toBeNull()
    expect(notice).toHaveTextContent('Sua imagem não foi avaliada')
    expect(screen.queryByText(QUALITY_ADVICE)).not.toBeInTheDocument()
    expect(screen.queryByText(OUTAGE_NOTICE)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Iniciar captura' })).toBeInTheDocument()
  })

  it.each([
    ['Pending', 'Wait', /verification service is unstable/],
    ['Expired', 'Recapture', /Your image was not evaluated/],
  ])('em en-US o estado %s usa o texto em inglês', async (state, nextStep, text) => {
    serveSelfie(() => ({ state, nextStep, feedbackCode: 'PROVIDER_UNAVAILABLE' }))

    await renderApp('/account/selfie', undefined, 'en-US')

    expect(await findNotice(text)).toBeInTheDocument()
    expect(screen.queryByText(/Adjust lighting/)).not.toBeInTheDocument()
  })

  it('sem o marcador a expiração continua pedindo ajuste de luz e enquadramento', async () => {
    serveSelfie(() => ({ state: 'Expired', nextStep: 'Recapture', feedbackCode: null }))

    await renderApp('/account/selfie')

    expect(await findNotice(QUALITY_ADVICE)).toBeInTheDocument()
    expect(screen.queryByText(EXPIRED_BY_OUTAGE_NOTICE)).not.toBeInTheDocument()
  })

  it('código desconhecido na espera não vira aviso de instabilidade', async () => {
    serveSelfie(() => ({ state: 'Pending', nextStep: 'Wait', feedbackCode: 'NOVO_CODIGO' }))

    await renderApp('/account/selfie')

    expect(await findNotice(/Aguardando verificação/)).toBeInTheDocument()
    expect(screen.queryByText(OUTAGE_NOTICE)).not.toBeInTheDocument()
  })

  it('quando o serviço volta, o aviso some na leitura seguinte', async () => {
    let feedbackCode: string | null = 'PROVIDER_UNAVAILABLE'
    serveSelfie(() => ({ state: 'Capturing', nextStep: 'Capture', feedbackCode }))
    await renderApp('/account/selfie')
    await findNotice(OUTAGE_NOTICE)

    feedbackCode = null

    await vi.waitFor(
      () => {
        expect(screen.queryByText(OUTAGE_NOTICE)).not.toBeInTheDocument()
      },
      { timeout: 5000 },
    )
  })
})
