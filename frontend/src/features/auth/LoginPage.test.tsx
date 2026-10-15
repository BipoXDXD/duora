import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { meQueryKey } from '@/features/session/query-keys'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, renderApp, type RenderedApp } from '@/test/render'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
}

function useLoginEndpoint(respond: () => Response) {
  const received: { body: unknown; token: string | null }[] = []
  server.use(
    http.post(`${API}/auth/login`, async ({ request }) => {
      received.push({ body: await request.json(), token: request.headers.get('X-XSRF-TOKEN') })
      return respond()
    }),
  )
  return received
}

function useAccountEndpoints() {
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(ME)),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
  )
}

async function fillAndSubmit(user: RenderedApp['user'], email: string, password: string) {
  if (email !== '') {
    await user.type(screen.getByLabelText('E-mail'), email)
  }
  if (password !== '') {
    await user.type(screen.getByLabelText('Senha'), password)
  }
  await user.click(screen.getByRole('button', { name: 'Entrar' }))
}

describe('tela de entrada', () => {
  it('tem título, label persistente e autocomplete de credenciais', async () => {
    await renderApp('/entrar')

    expect(screen.getByRole('heading', { level: 1, name: 'Entrar' })).toBeInTheDocument()
    expect(document.title).toBe('Entrar · Duora')
    expect(screen.getByLabelText('E-mail')).toHaveAttribute('autocomplete', 'email')
    expect(screen.getByLabelText('Senha')).toHaveAttribute('autocomplete', 'current-password')
  })

  it('entra, envia o token antiforgery e abre a área autenticada', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    useAccountEndpoints()
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, ' ana@exemplo.com ', 'frase longa de senha')

    expect(await screen.findByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    expect(received).toEqual([
      { body: { email: 'ana@exemplo.com', password: 'frase longa de senha' }, token: 'token-1' },
    ])
    // Um token antes do POST e outro depois, já ligado à identidade nova.
    expect(antiforgery.calls()).toBe(2)
  })

  it('descarta o cache da identidade anterior ao entrar (T-21)', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    useAccountEndpoints()
    const queryClient = createTestQueryClient()
    queryClient.setQueryData(['account', 'identidade-anterior', 'sessions'], [{ id: 'x' }])
    const { user } = await renderApp('/entrar', queryClient)

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    await screen.findByRole('heading', { level: 1, name: 'Sua conta' })
    expect(queryClient.getQueryData(['account', 'identidade-anterior', 'sessions'])).toBeUndefined()
  })

  it('volta ao caminho interno de onde veio', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    useAccountEndpoints()
    const { user } = await renderApp({ pathname: '/entrar', state: { from: '/conta?aba=sessoes' } })

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta\?aba=sessoes$/)
    })
  })

  it('caminho interno fora da allowlist de retorno leva à conta', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    useAccountEndpoints()
    const { user } = await renderApp({ pathname: '/entrar', state: { from: '/admin/usuarios' } })

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta$/)
    })
  })

  it('credenciais inválidas: erro neutro, sem apagar o que foi digitado', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => problem(401, 'INVALID_CREDENTIALS'))
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'ana@exemplo.com', 'senha-errada-longa')

    expect(await screen.findByRole('alert')).toHaveTextContent('E-mail ou senha incorretos.')
    expect(screen.getByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
    expect(screen.getByLabelText('Senha')).toHaveValue('senha-errada-longa')
  })

  it('segundo fator exigido: mensagem neutra de fluxo indisponível', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => problem(401, 'MFA_REQUIRED'))
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('segundo fator de autenticação')
    expect(screen.queryByRole('heading', { name: 'Sua conta' })).not.toBeInTheDocument()
  })

  it('429 mostra o tempo do Retry-After', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '90' }))
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 2 minutos antes de tentar de novo')
  })

  it('erro de rede tem mensagem própria', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => HttpResponse.error())
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
  })

  it('campos vazios: erros em texto e foco no primeiro campo inválido, sem chamar a API', async () => {
    const received = useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, '', '')

    const email = screen.getByLabelText('E-mail')
    expect(email).toHaveFocus()
    expect(email).toHaveAccessibleDescription('Informe seu e-mail.')
    expect(screen.getByLabelText('Senha')).toHaveAccessibleDescription('Informe sua senha.')
    expect(received).toEqual([])
  })

  it('foca o primeiro inválido e mantém o campo válido preenchido', async () => {
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'ana@exemplo.com', '')

    expect(screen.getByLabelText('Senha')).toHaveFocus()
    expect(screen.getByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
  })

  it.each([
    ['session-expired', 'Sua sessão expirou.'],
    ['sign-in-required', 'Entre para continuar.'],
    ['signed-out', 'Você saiu da sua conta.'],
  ])('mostra o aviso %s vindo da navegação', async (notice, text) => {
    await renderApp({ pathname: '/entrar', state: { notice } })

    expect(screen.getByRole('status')).toHaveTextContent(text)
  })

  it('oferece os caminhos de recuperação e cadastro', async () => {
    await renderApp('/entrar')

    expect(screen.getByRole('link', { name: 'Esqueci minha senha' })).toHaveAttribute('href', '/esqueci-a-senha')
    expect(screen.getByRole('link', { name: 'Criar conta' })).toHaveAttribute('href', '/criar-conta')
  })

  it('ignora meQueryKey antigo no cache: a área consulta a identidade de novo', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => new HttpResponse(null, { status: 204 }))
    useAccountEndpoints()
    const queryClient = createTestQueryClient()
    queryClient.setQueryData(meQueryKey, { ...ME, email: 'outra@exemplo.com' })
    const { user } = await renderApp('/entrar', queryClient)

    await fillAndSubmit(user, 'ana@exemplo.com', 'frase longa de senha')

    expect(await screen.findByText('ana@exemplo.com')).toBeInTheDocument()
    expect(screen.queryByText('outra@exemplo.com')).not.toBeInTheDocument()
  })
})
