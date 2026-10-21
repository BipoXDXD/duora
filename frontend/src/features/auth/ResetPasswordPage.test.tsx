import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { meQueryKey } from '@/features/session/query-keys'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, renderApp, type RenderedApp } from '@/test/render'

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const LINK = `/reset-password#userId=${USER_ID}&token=reset-token`

function useResetEndpoint(respond: () => Response) {
  const received: { body: unknown; token: string | null }[] = []
  server.use(
    http.post(`${API}/auth/password-resets`, async ({ request }) => {
      received.push({ body: await request.json(), token: request.headers.get('X-XSRF-TOKEN') })
      return respond()
    }),
  )
  return received
}

async function submitPassword({ user }: RenderedApp, password: string) {
  if (password !== '') {
    await user.click(screen.getByLabelText('Senha nova'))
    await user.paste(password)
  }
  await user.click(screen.getByRole('button', { name: 'Salvar senha nova' }))
}

const noContent = () => new HttpResponse(null, { status: 204 })

describe('tela de redefinição de senha', () => {
  it('usa autocomplete de senha nova', async () => {
    await renderApp(LINK)

    expect(document.title).toBe('Criar senha nova · Duora')
    expect(screen.getByLabelText('Senha nova')).toHaveAttribute('autocomplete', 'new-password')
  })

  it('envia userId, token e a senha nova com CSRF, e descarta o cache', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useResetEndpoint(noContent)
    const queryClient = createTestQueryClient()
    queryClient.setQueryData(meQueryKey, { id: USER_ID })
    const app = await renderApp(LINK, queryClient)

    await submitPassword(app, 'outra frase longa de senha')

    const success = await screen.findByRole('status')
    expect(success).toHaveTextContent('Senha alterada.')
    expect(success).toHaveFocus()
    expect(received).toEqual([
      {
        body: { userId: USER_ID, token: 'reset-token', newPassword: 'outra frase longa de senha' },
        token: antiforgery.latestToken(),
      },
    ])
    expect(queryClient.getQueryCache().getAll()).toEqual([])
  })

  it('tira userId e token do fragmento ao montar e ainda envia o token certo no POST', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useResetEndpoint(noContent)
    const app = await renderApp(LINK)

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/reset-password$/)
    })
    await submitPassword(app, 'outra frase longa de senha')

    expect(await screen.findByRole('status')).toHaveTextContent('Senha alterada.')
    expect(received).toEqual([
      {
        body: { userId: USER_ID, token: 'reset-token', newPassword: 'outra frase longa de senha' },
        token: antiforgery.latestToken(),
      },
    ])
  })

  it.each([
    [11, false],
    [12, true],
    [128, true],
    [129, false],
  ])('senha nova com %i caracteres é aceita no cliente? %s', async (length, valid) => {
    useAntiforgeryEndpoint()
    const received = useResetEndpoint(noContent)
    const app = await renderApp(LINK)

    await submitPassword(app, 'b'.repeat(length))

    if (valid) {
      expect(await screen.findByRole('status')).toHaveTextContent('Senha alterada.')
      expect(received).toHaveLength(1)
    } else {
      const input = screen.getByLabelText('Senha nova')
      expect(input).toHaveFocus()
      expect(input).toHaveAttribute('aria-invalid', 'true')
      expect(received).toEqual([])
    }
  })

  it('INVALID_TOKEN explica e oferece pedir um link novo', async () => {
    useAntiforgeryEndpoint()
    useResetEndpoint(() => problem(400, 'INVALID_TOKEN'))
    const app = await renderApp(LINK)

    await submitPassword(app, 'outra frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('Peça um link novo.')
    expect(screen.getByRole('link', { name: 'Pedir um link novo' })).toHaveAttribute('href', '/forgot-password')
  })

  it('429 mostra o tempo de espera', async () => {
    useAntiforgeryEndpoint()
    useResetEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '45' }))
    const app = await renderApp(LINK)

    await submitPassword(app, 'outra frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 45 segundos')
    expect(screen.getByLabelText('Senha nova')).toHaveValue('outra frase longa de senha')
  })

  it('erro de rede tem mensagem própria', async () => {
    useAntiforgeryEndpoint()
    useResetEndpoint(() => HttpResponse.error())
    const app = await renderApp(LINK)

    await submitPassword(app, 'outra frase longa de senha')

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
  })

  it('link malformado não mostra o formulário', async () => {
    await renderApp('/reset-password#userId=nao-e-uuid&token=x')

    expect(screen.getByRole('alert')).toHaveTextContent('Este link de recuperação é inválido ou expirou.')
    expect(screen.queryByLabelText('Senha nova')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Pedir um link novo' })).toHaveAttribute('href', '/forgot-password')
  })
})
