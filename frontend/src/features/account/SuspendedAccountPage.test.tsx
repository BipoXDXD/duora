import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const ACCOUNT = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
  currentTermsAccepted: false,
}

const SUSPENDED = {
  ...ACCOUNT,
  status: 'Suspended',
  suspension: { reasonCode: 'TermsViolation', suspendedAt: '2026-10-30T12:00:00Z' },
}

function useBackend(
  me: typeof ACCOUNT & { readonly status: string; readonly suspension?: object },
  sessions: () => Response = () => HttpResponse.json({ items: [] }),
) {
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/me/sessions`, sessions),
  )
  useAntiforgeryEndpoint()
}

describe('tela da conta suspensa (#299)', () => {
  it('mostra o motivo e o instante da suspensão, sem detalhe interno', async () => {
    useBackend(SUSPENDED)
    const { container } = await renderApp('/account/suspended')

    expect(await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeInTheDocument()
    expect(screen.getByText('Violação dos Termos de uso')).toBeInTheDocument()
    expect(container.querySelector('time')).toHaveAttribute('datetime', '2026-10-30T12:00:00Z')
    expect(container).not.toHaveTextContent('internalReason')
    expect(screen.getByText('O pedido de revisão desta medida ainda não está disponível pelo app.')).toBeInTheDocument()
  })

  it('leva qualquer área com sessão para a tela da conta suspensa', async () => {
    useBackend(SUSPENDED)
    await renderApp('/events')

    expect(await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeInTheDocument()
  })

  it('não mostra o aviso de novo aceite dos Termos, que a conta suspensa não pode atender', async () => {
    useBackend(SUSPENDED)
    await renderApp('/account/suspended')

    await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
  })

  it('mantém as sessões e a saída ao alcance', async () => {
    useBackend(SUSPENDED)
    await renderApp('/account/suspended')

    expect(await screen.findByRole('heading', { level: 2, name: 'Sessões ativas' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair' })).toBeInTheDocument()
  })

  it('mostra erro com nova tentativa quando as sessões não carregam', async () => {
    useBackend(SUSPENDED, () => problem(503, 'TEMPORARILY_UNAVAILABLE'))
    await renderApp('/account/suspended')

    expect(await screen.findByRole('button', { name: 'Tentar de novo' })).toBeInTheDocument()
  })

  it('usa um texto genérico para motivo que o app não conhece', async () => {
    useBackend({ ...SUSPENDED, suspension: { reasonCode: 'SomethingNew', suspendedAt: '2026-10-30T12:00:00Z' } })
    await renderApp('/account/suspended')

    expect(await screen.findByText('Indisponível no momento')).toBeInTheDocument()
  })

  it('devolve a conta que não está suspensa para a área da conta', async () => {
    useBackend({ ...ACCOUNT, status: 'Active', currentTermsAccepted: true })
    await renderApp('/account/suspended')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
  })

  it('oferece as telas da allowlist e as deixa abrir sem voltar para a suspensão', async () => {
    useBackend(SUSPENDED)
    server.use(http.get(`${API}/me/photos`, () => HttpResponse.json({ items: [], nextCursor: null })))
    const { user } = await renderApp('/account/suspended')

    await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })
    expect(screen.getByRole('link', { name: 'Autenticação em duas etapas' })).toHaveAttribute(
      'href',
      '/account/enable-mfa',
    )
    await user.click(screen.getByRole('link', { name: 'Minhas fotos' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Minhas fotos' })).toBeInTheDocument()
  })

  it('abre a tela de MFA para a conta suspensa', async () => {
    useBackend(SUSPENDED)
    await renderApp('/account/enable-mfa')

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Ative a autenticação em duas etapas' }),
    ).toBeInTheDocument()
  })

  it('com a sessão aberta, um 403 ACCOUNT_SUSPENDED revalida a conta e leva à tela da suspensão', async () => {
    let suspended = false
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(
          withSessionContext(suspended ? SUSPENDED : { ...ACCOUNT, status: 'Active', currentTermsAccepted: true }),
        ),
      ),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
      http.get(`${API}/me/profile`, () => {
        suspended = true
        return problem(403, 'ACCOUNT_SUSPENDED')
      }),
    )
    useAntiforgeryEndpoint()
    await renderApp('/account/profile')

    expect(await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeInTheDocument()
    expect(screen.getByText('Esta área não está disponível para a sua conta agora.')).toBeInTheDocument()
  })

  it('não mostra o aviso de área indisponível a quem chega pela própria conta', async () => {
    useBackend(SUSPENDED)
    await renderApp('/account')

    await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' })
    expect(screen.queryByText('Esta área não está disponível para a sua conta agora.')).not.toBeInTheDocument()
  })

  it('esconde o histórico de revisão das fotos, que a conta suspensa não alcança', async () => {
    useBackend(SUSPENDED)
    const photoId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
    server.use(
      http.get(`${API}/me/photos`, () =>
        HttpResponse.json({
          items: [{ photoId, status: 'Approved', rejectionCode: null, createdAt: '2026-10-30T12:00:00Z' }],
          nextCursor: null,
        }),
      ),
    )
    await renderApp('/account/photos')

    await screen.findByRole('heading', { level: 1, name: 'Minhas fotos' })
    expect(await screen.findByText('Aprovada')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: new RegExp(photoId) })).not.toBeInTheDocument()
  })
})
