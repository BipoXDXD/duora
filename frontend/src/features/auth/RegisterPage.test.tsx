import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, type RenderedApp } from '@/test/render'
import { syntheticLegalCatalog } from '@/test/legal-documents'

function useRegisterEndpoint(respond: () => Response) {
  const received: { body: unknown; token: string | null }[] = []
  server.use(
    http.post(`${API}/auth/register`, async ({ request }) => {
      received.push({ body: await request.json(), token: request.headers.get('X-XSRF-TOKEN') })
      return respond()
    }),
  )
  return received
}

interface RegisterInput {
  readonly email?: string
  readonly password?: string
  readonly acceptTerms?: boolean
  readonly acceptPrivacy?: boolean
  readonly confirmAdult?: boolean
}

async function fillAndSubmit({ user }: RenderedApp, input: RegisterInput) {
  const {
    email = 'ana@exemplo.com',
    password = 'frase longa de senha',
    acceptTerms = true,
    acceptPrivacy = true,
    confirmAdult = true,
  } = input
  await screen.findByRole('button', { name: 'Criar conta' })
  if (email !== '') {
    await user.type(await screen.findByLabelText('E-mail'), email)
  }
  if (password !== '') {
    // Colar, como faz um gerenciador de senhas.
    await user.click(await screen.findByLabelText('Senha'))
    await user.paste(password)
  }
  if (acceptTerms) {
    await user.click(screen.getByRole('checkbox', { name: /Termos de uso/ }))
  }
  if (acceptPrivacy) {
    await user.click(screen.getByRole('checkbox', { name: /Política de privacidade/ }))
  }
  if (confirmAdult) {
    await user.click(screen.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' }))
  }
  await user.click(screen.getByRole('button', { name: 'Criar conta' }))
}

const accepted = () => new HttpResponse(null, { status: 202 })

describe('tela de cadastro', () => {
  it('keeps registration closed while the public legal documents are unavailable', async () => {
    server.use(
      http.get(`${API}/legal-documents`, () =>
        HttpResponse.json({
          ...syntheticLegalCatalog,
          registrationAvailable: false,
          documents: [],
        }),
      ),
    )
    await renderApp('/register')
    expect(await screen.findByRole('alert')).toHaveTextContent('O cadastro está indisponível')
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Criar conta' })).not.toBeInTheDocument()
  })

  it('rechecks publication when returning after a draft has been approved', async () => {
    server.use(
      http.get(`${API}/legal-documents`, () =>
        HttpResponse.json({
          ...syntheticLegalCatalog,
          registrationAvailable: false,
        }),
      ),
    )
    const app = await renderApp('/register')
    expect(await screen.findByRole('alert')).toHaveTextContent('O cadastro está indisponível')
    server.use(http.get(`${API}/legal-documents`, () => HttpResponse.json(syntheticLegalCatalog)))
    await app.user.click(screen.getByRole('link', { name: 'Duora' }))
    await app.user.click(await screen.findByRole('link', { name: 'Criar conta' }))
    expect(await screen.findByLabelText('Senha')).toBeInTheDocument()
  })

  it('binds the English documents explicitly offered in the English interface', async () => {
    useAntiforgeryEndpoint()
    const received = useRegisterEndpoint(accepted)
    const { user } = await renderApp('/register', undefined, 'en-US')
    await user.type(await screen.findByLabelText('Email'), 'reader@example.com')
    await user.type(await screen.findByLabelText('Password'), 'long enough password')
    for (const checkbox of screen.getAllByRole('checkbox')) await user.click(checkbox)
    await user.click(screen.getByRole('button', { name: 'Create account' }))
    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument()
    expect(received[0]?.body).toMatchObject({
      acceptedTermsLocale: 'en-US',
      acceptedPrivacyLocale: 'en-US',
      confirmedAdult: true,
    })
  })

  it('resets acceptance when a document language changes and sends independent metadata', async () => {
    useAntiforgeryEndpoint()
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')
    await screen.findByLabelText('E-mail')
    await app.user.click(screen.getByRole('checkbox', { name: /Termos de uso/ }))
    await app.user.selectOptions(screen.getByLabelText('Idioma da Política de privacidade'), 'en-US')
    expect(screen.getByRole('checkbox', { name: /Termos de uso/ })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: /Política de privacidade/ })).not.toBeChecked()
    await fillAndSubmit(app, {})
    expect(await screen.findByRole('heading', { name: 'Verifique seu e-mail' })).toBeInTheDocument()
    expect(received[0]?.body).toMatchObject({
      acceptedTermsLocale: 'pt-BR',
      acceptedPrivacyLocale: 'en-US',
      acceptedTermsSha256: '1'.repeat(64),
      acceptedPrivacySha256: '4'.repeat(64),
    })
  })

  it('cancels the previous acceptance if documents change while CSRF is pending', async () => {
    let release!: () => void
    let started!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const began = new Promise<void>((resolve) => {
      started = resolve
    })
    server.use(
      http.get(`${API}/auth/csrf`, async () => {
        started()
        await gate
        document.cookie = 'XSRF-TOKEN=synthetic-token; path=/'
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')
    await fillAndSubmit(app, {})
    await began
    await app.user.selectOptions(screen.getByLabelText('Idioma dos Termos de uso'), 'en-US')
    release()
    await waitFor(() => expect(screen.queryByText('Criando conta…')).not.toBeInTheDocument())
    // Waiting for the old request to finish cannot carry its previous acceptance into the new form.
    await app.user.click(screen.getByRole('button', { name: 'Criar conta' }))
    expect(screen.getByRole('checkbox', { name: /Termos de uso/ })).not.toBeChecked()
    expect(received).toHaveLength(0)
    expect(screen.getByTestId('location')).toHaveTextContent('/register')
  })

  it('usa autocomplete de e-mail e de senha nova', async () => {
    await renderApp('/register')

    expect(document.title).toBe('Criar conta · Duora')
    expect(await screen.findByLabelText('E-mail')).toHaveAttribute('autocomplete', 'email')
    expect(await screen.findByLabelText('Senha')).toHaveAttribute('autocomplete', 'new-password')
    expect(await screen.findByLabelText('Senha')).toHaveAccessibleDescription(/Entre 12 e 128 caracteres/)
  })

  it('envia e-mail, senha e as versões aceitas, com CSRF, e leva à tela neutra', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('heading', { level: 1, name: 'Verifique seu e-mail' })).toBeInTheDocument()
    expect(received).toEqual([
      {
        body: {
          email: 'ana@exemplo.com',
          password: 'frase longa de senha',
          acceptedTermsVersion: syntheticLegalCatalog.termsVersion,
          acceptedPrivacyVersion: syntheticLegalCatalog.privacyVersion,
          acceptedTermsLocale: 'pt-BR',
          acceptedTermsSha256: '1'.repeat(64),
          acceptedPrivacySha256: '3'.repeat(64),
          acceptedPrivacyLocale: 'pt-BR',
          confirmedAdult: true,
        },
        token: antiforgery.latestToken(),
      },
    ])
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/check-email$/)
  })

  it.each([
    [11, false],
    [12, true],
    [128, true],
    [129, false],
  ])('senha com %i caracteres é aceita no cliente? %s', async (length, valid) => {
    useAntiforgeryEndpoint()
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')

    await fillAndSubmit(app, { password: 'a'.repeat(length) })

    if (valid) {
      expect(await screen.findByRole('heading', { name: 'Verifique seu e-mail' })).toBeInTheDocument()
      expect(received).toHaveLength(1)
    } else {
      const password = await screen.findByLabelText('Senha')
      expect(password).toHaveFocus()
      expect(password).toHaveAttribute('aria-invalid', 'true')
      expect(password).toHaveAccessibleDescription(
        length < 12 ? /pelo menos 12 caracteres/ : /no máximo 128 caracteres/,
      )
      expect(received).toEqual([])
    }
  })

  it('exige os dois aceites, com erro em cada caixa e foco na primeira', async () => {
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')

    await fillAndSubmit(app, { acceptTerms: false, acceptPrivacy: false })

    const terms = screen.getByRole('checkbox', { name: /Termos de uso/ })
    expect(terms).toHaveFocus()
    expect(terms).toHaveAccessibleDescription('Para criar a conta, aceite os Termos de uso.')
    expect(screen.getByRole('checkbox', { name: /Política de privacidade/ })).toHaveAccessibleDescription(
      'Para criar a conta, confirme que leu a Política de privacidade.',
    )
    expect(await screen.findByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
    expect(received).toEqual([])
  })

  it('exige a declaração de 18 anos ou mais e não envia o cadastro sem ela', async () => {
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')

    await fillAndSubmit(app, { confirmAdult: false })

    const adult = screen.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' })
    expect(adult).toHaveFocus()
    expect(adult).toHaveAttribute('aria-invalid', 'true')
    expect(adult).toHaveAccessibleDescription('Para criar a conta, declare que tem 18 anos ou mais.')
    expect(received).toEqual([])
  })

  it('pede só a declaração, sem data de nascimento nem idade', async () => {
    await renderApp('/register')

    await screen.findByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' })
    expect(screen.queryByLabelText(/nascimento|\bidade\b/i)).not.toBeInTheDocument()
    expect(document.querySelector('input[type="date"], input[type="number"]')).toBeNull()
  })

  it('shows the adult declaration in the English interface', async () => {
    await renderApp('/register', undefined, 'en-US')

    expect(await screen.findByRole('checkbox', { name: 'I declare that I am 18 or older' })).not.toBeChecked()
  })

  it('e-mail inválido: erro associado e foco no campo', async () => {
    const app = await renderApp('/register')

    await fillAndSubmit(app, { email: 'ana@' })

    const email = await screen.findByLabelText('E-mail')
    expect(email).toHaveFocus()
    expect(email).toHaveAccessibleDescription('Informe um e-mail no formato nome@exemplo.com.')
  })

  it('429 mostra o tempo de espera e mantém o formulário preenchido', async () => {
    useAntiforgeryEndpoint()
    useRegisterEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '30' }))
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 30 segundos')
    expect(await screen.findByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
    expect(await screen.findByLabelText('Senha')).toHaveValue('frase longa de senha')
  })

  it('erro de rede tem mensagem própria', async () => {
    useAntiforgeryEndpoint()
    useRegisterEndpoint(() => HttpResponse.error())
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
  })

  it('falha ao obter o token antiforgery não envia o cadastro', async () => {
    server.use(http.get(`${API}/auth/csrf`, () => HttpResponse.error()))
    const received = useRegisterEndpoint(accepted)
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(received).toEqual([])
  })

  it('erro inesperado do servidor usa a mensagem genérica', async () => {
    useAntiforgeryEndpoint()
    useRegisterEndpoint(() => problem(500, 'INTERNAL'))
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('alert')).toHaveTextContent('Algo deu errado do nosso lado.')
  })
})

describe('tela "verifique seu e-mail"', () => {
  it('é neutra: não confirma que o endereço tem conta', async () => {
    await renderApp('/check-email')

    expect(screen.getByRole('heading', { level: 1, name: 'Verifique seu e-mail' })).toBeInTheDocument()
    expect(screen.getByText(/Se for possível criar a conta/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ir para a entrada' })).toHaveAttribute('href', '/login')
  })
})
