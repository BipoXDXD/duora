import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, type RenderedApp } from '@/test/render'

function useResetRequestEndpoint(respond: () => Response) {
  const received: { body: unknown; token: string | null }[] = []
  server.use(
    http.post(`${API}/auth/password-reset-requests`, async ({ request }) => {
      received.push({ body: await request.json(), token: request.headers.get('X-XSRF-TOKEN') })
      return respond()
    }),
  )
  return received
}

async function submitEmail({ user }: RenderedApp, email: string) {
  if (email !== '') {
    await user.type(screen.getByLabelText('E-mail'), email)
  }
  await user.click(screen.getByRole('button', { name: 'Enviar link' }))
}

describe('tela de recuperação de senha', () => {
  it('pede o link com CSRF e responde de forma neutra', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    const received = useResetRequestEndpoint(() => new HttpResponse(null, { status: 202 }))
    const app = await renderApp('/forgot-password')

    expect(screen.getByLabelText('E-mail')).toHaveAttribute('autocomplete', 'email')
    await submitEmail(app, 'ana@exemplo.com')

    const success = await screen.findByRole('status')
    expect(success).toHaveTextContent('Se houver uma conta com esse e-mail')
    // O botão some com o formulário: o foco vai para o aviso, não cai no body.
    expect(success).toHaveFocus()
    expect(received).toEqual([{ body: { email: 'ana@exemplo.com' }, token: antiforgery.latestToken() }])
  })

  it('e-mail vazio: erro associado, foco no campo e nada enviado', async () => {
    const received = useResetRequestEndpoint(() => new HttpResponse(null, { status: 202 }))
    const app = await renderApp('/forgot-password')

    await submitEmail(app, '')

    const email = screen.getByLabelText('E-mail')
    expect(email).toHaveFocus()
    expect(email).toHaveAccessibleDescription('Informe seu e-mail.')
    expect(received).toEqual([])
  })

  it('429 mostra o tempo de espera', async () => {
    useAntiforgeryEndpoint()
    useResetRequestEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '600' }))
    const app = await renderApp('/forgot-password')

    await submitEmail(app, 'ana@exemplo.com')

    expect(await screen.findByRole('alert')).toHaveTextContent('espere 10 minutos')
    expect(screen.getByLabelText('E-mail')).toHaveValue('ana@exemplo.com')
  })

  it('erro de rede tem mensagem própria', async () => {
    useAntiforgeryEndpoint()
    useResetRequestEndpoint(() => HttpResponse.error())
    const app = await renderApp('/forgot-password')

    await submitEmail(app, 'ana@exemplo.com')

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
  })
})
