import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, type RenderedApp } from '@/test/render'

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const LINK = `/confirmar-email#userId=${USER_ID}&token=${encodeURIComponent('tok+en/=')}`
const PASSWORD = 'frase longa do cadastro'

function useConfirmEndpoint(respond: () => Response) {
  const received: { body: unknown; token: string | null }[] = []
  server.use(
    http.post(`${API}/auth/confirm-email`, async ({ request }) => {
      received.push({ body: await request.json(), token: request.headers.get('X-XSRF-TOKEN') })
      return respond()
    }),
  )
  return received
}

async function confirmWithPassword({ user }: RenderedApp, password: string) {
  if (password !== '') {
    // Colar, como faz um gerenciador de senhas.
    await user.click(screen.getByLabelText('Senha'))
    await user.paste(password)
  }
  await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))
}

const noContent = () => new HttpResponse(null, { status: 204 })

describe('tela de confirmação de e-mail', () => {
  it('não confirma sozinha ao abrir: espera o clique (scanner de link não confirma)', async () => {
    const received = useConfirmEndpoint(noContent)
    await renderApp(LINK)

    expect(document.title).toBe('Confirmar e-mail · Duora')
    expect(screen.getByRole('button', { name: 'Confirmar e-mail' })).toBeInTheDocument()
    expect(received).toEqual([])
  })

  it('pede a senha do cadastro com label persistente, ajuda e autocomplete de senha atual', async () => {
    await renderApp(LINK)

    const password = screen.getByLabelText('Senha')
    expect(password).toHaveAttribute('type', 'password')
    expect(password).toHaveAttribute('autocomplete', 'current-password')
    expect(password).toHaveAccessibleDescription('Use a mesma senha que você criou no cadastro.')
  })

  it('confirma com userId, token e a senha colada, com CSRF', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useConfirmEndpoint(noContent)
    const app = await renderApp(LINK)

    await confirmWithPassword(app, PASSWORD)

    const success = await screen.findByRole('status')
    expect(success).toHaveTextContent('E-mail confirmado.')
    expect(success).toHaveFocus()
    expect(screen.getByRole('link', { name: 'Entrar' })).toHaveAttribute('href', '/entrar')
    expect(received).toEqual([
      { body: { userId: USER_ID, token: 'tok+en/=', password: PASSWORD }, token: antiforgery.latestToken() },
    ])
  })

  it('tira userId e token do fragmento ao montar e ainda envia o token certo no POST', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useConfirmEndpoint(noContent)
    const app = await renderApp(LINK)

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/confirmar-email$/)
    })
    await confirmWithPassword(app, PASSWORD)

    expect(await screen.findByRole('status')).toHaveTextContent('E-mail confirmado.')
    expect(received).toEqual([
      { body: { userId: USER_ID, token: 'tok+en/=', password: PASSWORD }, token: antiforgery.latestToken() },
    ])
  })

  it('senha vazia: erro associado, foco no campo e nada enviado', async () => {
    const received = useConfirmEndpoint(noContent)
    const app = await renderApp(LINK)

    await confirmWithPassword(app, '')

    const password = screen.getByLabelText('Senha')
    expect(password).toHaveFocus()
    expect(password).toHaveAttribute('aria-invalid', 'true')
    expect(password).toHaveAccessibleDescription('Use a mesma senha que você criou no cadastro. Informe sua senha.')
    expect(received).toEqual([])
  })

  it('um link com token na query também sai da URL, mesmo recusado', async () => {
    await renderApp(`/confirmar-email?userId=${USER_ID}&token=abc`)

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/confirmar-email$/)
    })
  })

  it('INVALID_TOKEN (token ruim ou senha errada): mensagem neutra e caminho para cadastrar de novo', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => problem(400, 'INVALID_TOKEN'))
    const app = await renderApp(LINK)

    await confirmWithPassword(app, 'senha errada qualquer')

    expect(await screen.findByRole('alert')).toHaveTextContent('Link inválido ou senha incorreta.')
    expect(screen.getByRole('link', { name: 'Criar conta de novo' })).toHaveAttribute('href', '/criar-conta')
    expect(screen.getByLabelText('Senha')).toHaveValue('senha errada qualquer')
  })

  it('429 mostra o tempo de espera', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '1' }))
    const app = await renderApp(LINK)

    await confirmWithPassword(app, PASSWORD)

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 1 segundo')
  })

  it('erro de rede tem mensagem própria e permite tentar de novo', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => HttpResponse.error())
    const app = await renderApp(LINK)

    await confirmWithPassword(app, PASSWORD)

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(screen.getByRole('button', { name: 'Confirmar e-mail' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Criar conta de novo' })).not.toBeInTheDocument()
  })

  it.each([
    ['sem parâmetros', '/confirmar-email'],
    ['sem token', `/confirmar-email#userId=${USER_ID}`],
    ['userId inválido', '/confirmar-email#userId=123&token=abc'],
    ['formato antigo, na query', `/confirmar-email?userId=${USER_ID}&token=abc`],
  ])('link malformado (%s) não chama a API', async (_caso, path) => {
    const received = useConfirmEndpoint(noContent)
    await renderApp(path)

    expect(screen.getByRole('alert')).toHaveTextContent('Este link de confirmação é inválido ou expirou.')
    expect(screen.queryByRole('button', { name: 'Confirmar e-mail' })).not.toBeInTheDocument()
    expect(received).toEqual([])
  })
})

describe('confirmação por código', () => {
  it('pede o código digitável e envia no corpo, sem confirmação automática', async () => {
    useAntiforgeryEndpoint()
    const received = useConfirmEndpoint(noContent)
    const app = await renderApp(`/confirmar-email#userId=${USER_ID}&token=code`)
    const input = screen.getByLabelText('Código de confirmação')
    expect(input).toHaveAttribute('autocomplete', 'one-time-code')
    expect(input).toHaveAttribute('inputmode', 'numeric')
    expect(received).toEqual([])
    await app.user.type(input, '0123 4567')
    await confirmWithPassword(app, PASSWORD)
    expect(await screen.findByRole('status')).toHaveTextContent('E-mail confirmado.')
    expect(received[0]?.body).toEqual({ userId: USER_ID, token: '01234567', password: PASSWORD })
  })

  it('código ausente mantém o foco no campo e não chama a API', async () => {
    const received = useConfirmEndpoint(noContent)
    const app = await renderApp(`/confirmar-email#userId=${USER_ID}&token=code`)
    await confirmWithPassword(app, PASSWORD)
    expect(screen.getByLabelText('Código de confirmação')).toHaveFocus()
    expect(screen.getByLabelText('Código de confirmação')).toHaveAttribute('aria-invalid', 'true')
    expect(received).toEqual([])
  })
})
