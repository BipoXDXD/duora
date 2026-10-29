import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { syntheticLegalCatalog } from '@/test/legal-documents'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const ACCOUNT = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}

const termsHash = (locale: 'pt-BR' | 'en-US'): string =>
  syntheticLegalCatalog.documents.find((document) => document.document === 'terms' && document.locale === locale)
    ?.sha256 ?? ''

/** Backend sintético: /me reflete o aceite, e o POST guarda os corpos recebidos. */
function useBackend({ accepted = false, failWith }: { accepted?: boolean; failWith?: string } = {}) {
  const state = { accepted, writes: [] as unknown[] }
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(withSessionContext({ ...ACCOUNT, currentTermsAccepted: state.accepted })),
    ),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    http.post(`${API}/me/terms-acceptances`, async ({ request }) => {
      state.writes.push(await request.json())
      if (failWith !== undefined) return problem(400, failWith)
      state.accepted = true
      return new HttpResponse(null, { status: 204 })
    }),
  )
  useAntiforgeryEndpoint()
  return state
}

describe('aviso de novo aceite dos Termos (#296)', () => {
  it('aparece para conta com aceite antigo sem bloquear a área', async () => {
    useBackend()
    await renderApp('/account')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    const notice = screen.getByRole('complementary', { name: 'Os Termos de uso mudaram' })
    expect(notice).toHaveTextContent('o Duora continua disponível')
    expect(screen.getByRole('link', { name: 'Revisar e aceitar' })).toHaveAttribute('href', '/account/terms')
  })

  it('não aparece para conta com o aceite em dia', async () => {
    useBackend({ accepted: true })
    await renderApp('/account')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
  })

  it('pode ser dispensado; o foco vai para o conteúdo principal e o aviso não volta ao navegar', async () => {
    useBackend()
    const { user } = await renderApp('/account')
    await screen.findByRole('heading', { level: 1, name: 'Sua conta' })

    await user.click(screen.getByRole('button', { name: 'Dispensar aviso' }))

    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
    expect(screen.getByRole('main')).toHaveFocus()
    await user.click(screen.getByRole('link', { name: 'Editar perfil' }))
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
  })
})

describe('conta privilegiada (#296, D13)', () => {
  it.each([true, false])(
    'não vê o aviso no console administrativo nem na própria conta (currentTermsAccepted %s)',
    async (currentTermsAccepted) => {
      server.use(
        http.get(`${API}/me`, () =>
          HttpResponse.json(
            withSessionContext({ ...ACCOUNT, roles: ['moderator'], mfaEnabled: true, currentTermsAccepted }),
          ),
        ),
        http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
        http.get(`${API}/admin/photo-reviews`, () => HttpResponse.json({ items: [] })),
      )
      useAntiforgeryEndpoint()
      const { user } = await renderApp('/account')
      expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
      expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()

      await user.click(screen.getByRole('link', { name: 'Editar perfil' }))
      expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
    },
  )

  it('não vê o aviso em /admin/photo-reviews', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(
          withSessionContext({ ...ACCOUNT, roles: ['moderator'], mfaEnabled: true, currentTermsAccepted: false }),
        ),
      ),
      http.get(`${API}/admin/photo-reviews/capabilities`, () =>
        HttpResponse.json({ assign: false, evidence: false, decide: false }),
      ),
      http.get(`${API}/admin/photo-reviews`, () => HttpResponse.json({ items: [], nextCursor: null })),
    )
    useAntiforgeryEndpoint()
    await renderApp('/admin/photo-reviews')

    expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
  })
})

describe('tela de novo aceite dos Termos (#296)', () => {
  it('grava o recibo dos Termos ativos com a declaração de 18+ e o aviso some', async () => {
    const backend = useBackend()
    const { user } = await renderApp('/account')
    await user.click(await screen.findByRole('link', { name: 'Revisar e aceitar' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Novo aceite dos Termos' })).toBeInTheDocument()
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Ler Synthetic Terms (versão test-terms-v2)' })).toHaveAttribute(
      'href',
      '/terms/test-terms-v2/pt-BR',
    )

    await user.click(screen.getByRole('checkbox', { name: 'Li e aceito os Termos de uso (versão test-terms-v2).' }))
    await user.click(screen.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' }))
    await user.click(screen.getByRole('button', { name: 'Confirmar aceite' }))

    const done = (await screen.findByText('Aceite registrado. Obrigado!')).closest('[role="status"]')
    await waitFor(() => {
      expect(done).toHaveFocus()
    })
    expect(backend.writes).toEqual([
      {
        acceptedTermsVersion: 'test-terms-v2',
        acceptedTermsLocale: 'pt-BR',
        acceptedTermsSha256: termsHash('pt-BR'),
        confirmedAdult: true,
      },
    ])
    await user.click(screen.getByRole('link', { name: 'Voltar para a conta' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    expect(screen.queryByRole('complementary', { name: 'Os Termos de uso mudaram' })).not.toBeInTheDocument()
  })

  it('envia o recibo do idioma do documento escolhido, independente do idioma da interface', async () => {
    const backend = useBackend()
    const { user } = await renderApp('/account/terms')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Idioma dos Termos de uso' }), 'en-US')

    await user.click(screen.getByRole('checkbox', { name: 'Li e aceito os Termos de uso (versão test-terms-v2).' }))
    await user.click(screen.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' }))
    await user.click(screen.getByRole('button', { name: 'Confirmar aceite' }))

    await screen.findByText('Aceite registrado. Obrigado!')
    expect(backend.writes).toEqual([
      expect.objectContaining({ acceptedTermsLocale: 'en-US', acceptedTermsSha256: termsHash('en-US') }),
    ])
  })

  it.each([
    [
      'sem a declaração de 18+',
      'Li e aceito os Termos de uso (versão test-terms-v2).',
      'Para registrar o novo aceite, declare que tem 18 anos ou mais.',
    ],
    ['sem aceitar os Termos', 'Declaro ter 18 anos ou mais', 'Para registrar o novo aceite, aceite os Termos de uso.'],
  ])('não envia nada %s', async (_caso, checked, message) => {
    const backend = useBackend()
    const { user } = await renderApp('/account/terms')
    await user.click(await screen.findByRole('checkbox', { name: checked }))

    await user.click(screen.getByRole('button', { name: 'Confirmar aceite' }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(backend.writes).toEqual([])
  })

  it('explica quando a versão mudou durante a leitura e mantém o aviso', async () => {
    useBackend({ failWith: 'VALIDATION_FAILED' })
    const { user } = await renderApp('/account/terms')
    await user.click(
      await screen.findByRole('checkbox', { name: 'Li e aceito os Termos de uso (versão test-terms-v2).' }),
    )
    await user.click(screen.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' }))

    await user.click(screen.getByRole('button', { name: 'Confirmar aceite' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Os Termos mudaram enquanto você lia.')
    await user.click(screen.getByRole('link', { name: 'Agora não, voltar para a conta' }))
    expect(await screen.findByRole('complementary', { name: 'Os Termos de uso mudaram' })).toBeInTheDocument()
  })

  it('mostra que o aceite está em dia quando não há nada a aceitar', async () => {
    useBackend({ accepted: true })
    await renderApp('/account/terms')

    expect(await screen.findByText('Seu aceite dos Termos de uso está em dia.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar aceite' })).not.toBeInTheDocument()
  })

  it('mostra indisponibilidade sem formulário quando a publicação não está aberta', async () => {
    useBackend()
    server.use(
      http.get('/api/v1/legal-documents', () =>
        HttpResponse.json({ ...syntheticLegalCatalog, registrationAvailable: false }),
      ),
    )
    await renderApp('/account/terms')

    expect(await screen.findByText(/Os Termos de uso não estão disponíveis agora/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar aceite' })).not.toBeInTheDocument()
  })
})
