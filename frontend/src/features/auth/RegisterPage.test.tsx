import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, type RenderedApp } from '@/test/render'
import { LEGAL_DOCUMENT_VERSIONS } from './auth-schemas'

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
}

async function fillAndSubmit({ user }: RenderedApp, input: RegisterInput) {
  const {
    email = 'ana@exemplo.com',
    password = 'frase longa de senha',
    acceptTerms = true,
    acceptPrivacy = true,
  } = input
  if (email !== '') {
    await user.type(screen.getByLabelText('E-mail'), email)
  }
  if (password !== '') {
    // Colar, como faz um gerenciador de senhas.
    await user.click(screen.getByLabelText('Senha'))
    await user.paste(password)
  }
  if (acceptTerms) {
    await user.click(screen.getByRole('checkbox', { name: /Termos de uso/ }))
  }
  if (acceptPrivacy) {
    await user.click(screen.getByRole('checkbox', { name: /Política de privacidade/ }))
  }
  await user.click(screen.getByRole('button', { name: 'Criar conta' }))
}

const accepted = () => new HttpResponse(null, { status: 202 })

describe('tela de cadastro', () => {
  it('does not claim English documents merely because the interface is English', async () => {
    useAntiforgeryEndpoint()
    const received = useRegisterEndpoint(accepted)
    const { user } = await renderApp('/register', undefined, 'en-US')
    await user.type(screen.getByLabelText('Email'), 'reader@example.com')
    await user.type(screen.getByLabelText('Password'), 'long enough password')
    for (const checkbox of screen.getAllByRole('checkbox')) await user.click(checkbox)
    await user.click(screen.getByRole('button', { name: 'Create account' }))
    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument()
    expect(received[0]?.body).toMatchObject({ acceptedTermsLocale: null, acceptedPrivacyLocale: null })
  })

  it('usa autocomplete de e-mail e de senha nova', async () => {
    await renderApp('/register')

    expect(document.title).toBe('Criar conta · Duora')
    expect(screen.getByLabelText('E-mail')).toHaveAttribute('autocomplete', 'email')
    expect(screen.getByLabelText('Senha')).toHaveAttribute('autocomplete', 'new-password')
    expect(screen.getByLabelText('Senha')).toHaveAccessibleDescription(/Entre 12 e 128 caracteres/)
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
          acceptedTermsVersion: LEGAL_DOCUMENT_VERSIONS.terms,
          acceptedPrivacyVersion: LEGAL_DOCUMENT_VERSIONS.privacy,
          acceptedTermsLocale: null,
          acceptedPrivacyLocale: null,
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
      const password = screen.getByLabelText('Senha')
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
    expect(screen.getByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
    expect(received).toEqual([])
  })

  it('e-mail inválido: erro associado e foco no campo', async () => {
    const app = await renderApp('/register')

    await fillAndSubmit(app, { email: 'ana@' })

    const email = screen.getByLabelText('E-mail')
    expect(email).toHaveFocus()
    expect(email).toHaveAccessibleDescription('Informe um e-mail no formato nome@exemplo.com.')
  })

  it('429 mostra o tempo de espera e mantém o formulário preenchido', async () => {
    useAntiforgeryEndpoint()
    useRegisterEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '30' }))
    const app = await renderApp('/register')

    await fillAndSubmit(app, {})

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 30 segundos')
    expect(screen.getByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
    expect(screen.getByLabelText('Senha')).toHaveValue('frase longa de senha')
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
