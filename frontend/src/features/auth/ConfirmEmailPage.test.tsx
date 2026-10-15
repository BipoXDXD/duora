import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const LINK = `/confirmar-email?userId=${USER_ID}&token=${encodeURIComponent('tok+en/=')}`

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

describe('tela de confirmação de e-mail', () => {
  it('não confirma sozinha ao abrir: espera o clique (scanner de link não confirma)', () => {
    const received = useConfirmEndpoint(() => new HttpResponse(null, { status: 204 }))
    renderApp(LINK)

    expect(document.title).toBe('Confirmar e-mail · Duora')
    expect(screen.getByRole('button', { name: 'Confirmar e-mail' })).toBeInTheDocument()
    expect(received).toEqual([])
  })

  it('confirma com userId e token do link, com CSRF', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useConfirmEndpoint(() => new HttpResponse(null, { status: 204 }))
    const { user } = renderApp(LINK)

    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))

    expect(await screen.findByRole('status')).toHaveTextContent('E-mail confirmado.')
    expect(screen.getByRole('link', { name: 'Entrar' })).toHaveAttribute('href', '/entrar')
    expect(received).toEqual([{ body: { userId: USER_ID, token: 'tok+en/=' }, token: antiforgery.latestToken() }])
  })

  it('tira userId e token da URL ao montar e ainda envia o token certo no POST', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useConfirmEndpoint(() => new HttpResponse(null, { status: 204 }))
    const { user } = renderApp(LINK)

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/confirmar-email$/)
    })
    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))

    expect(await screen.findByRole('status')).toHaveTextContent('E-mail confirmado.')
    expect(received).toEqual([{ body: { userId: USER_ID, token: 'tok+en/=' }, token: antiforgery.latestToken() }])
  })

  it('INVALID_TOKEN explica que o link não vale mais', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => problem(400, 'INVALID_TOKEN'))
    const { user } = renderApp(LINK)

    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('inválido, expirou ou já foi usado')
  })

  it('429 mostra o tempo de espera', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '1' }))
    const { user } = renderApp(LINK)

    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 1 segundo')
  })

  it('erro de rede tem mensagem própria e permite tentar de novo', async () => {
    useAntiforgeryEndpoint()
    useConfirmEndpoint(() => HttpResponse.error())
    const { user } = renderApp(LINK)

    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(screen.getByRole('button', { name: 'Confirmar e-mail' })).toBeInTheDocument()
  })

  it.each([
    ['sem parâmetros', '/confirmar-email'],
    ['sem token', `/confirmar-email?userId=${USER_ID}`],
    ['userId inválido', '/confirmar-email?userId=123&token=abc'],
  ])('link malformado (%s) não chama a API', (_caso, path) => {
    const received = useConfirmEndpoint(() => new HttpResponse(null, { status: 204 }))
    renderApp(path)

    expect(screen.getByRole('alert')).toHaveTextContent('Este link de confirmação é inválido ou expirou.')
    expect(screen.queryByRole('button', { name: 'Confirmar e-mail' })).not.toBeInTheDocument()
    expect(received).toEqual([])
  })
})
