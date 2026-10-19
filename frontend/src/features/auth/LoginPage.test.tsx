import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { meQueryKey } from '@/features/session/query-keys'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, renderApp, ROUTE_CHUNK_TIMEOUT_MS, type RenderedApp } from '@/test/render'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
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

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
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

    await screen.findByRole('heading', { level: 1, name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS })
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

  it('segundo fator exigido: mensagem neutra e campo para o código', async () => {
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

    expect(await screen.findByText('ana@exemplo.com', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toBeInTheDocument()
    expect(screen.queryByText('outra@exemplo.com')).not.toBeInTheDocument()
  })
})

it('envia o TOTP solicitado e entra com MFA ativo', async () => {
  useAntiforgeryEndpoint()
  let attempt = 0
  const received = useLoginEndpoint(() =>
    ++attempt === 1 ? problem(401, 'MFA_REQUIRED') : new HttpResponse(null, { status: 204 }),
  )
  useAccountEndpoints()
  const { user } = await renderApp('/entrar')
  await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
  await user.type(await screen.findByLabelText('Código do autenticador'), '123456')
  await user.click(screen.getByRole('button', { name: 'Entrar' }))
  expect(
    await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
  ).toBeInTheDocument()
  expect(received[1]?.body).toEqual({
    email: 'admin@exemplo.com',
    password: 'frase longa de senha',
    twoFactorCode: '123456',
  })
})

describe('segundo fator na entrada', () => {
  it.each(['', '12345', '1234567', 'abcdef'])('recusa o código %s sem chamar a API de novo', async (code) => {
    useAntiforgeryEndpoint()
    const received = useLoginEndpoint(() => problem(401, 'MFA_REQUIRED'))
    const { user } = await renderApp('/entrar')
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    const field = await screen.findByLabelText('Código do autenticador')
    if (code !== '') await user.type(field, code)

    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(field).toHaveAccessibleDescription('Informe o código de 6 dígitos do autenticador.')
    expect(received).toHaveLength(1)
  })

  it('o campo do código continua depois de um segundo erro', async () => {
    useAntiforgeryEndpoint()
    let attempt = 0
    useLoginEndpoint(() => (++attempt === 1 ? problem(401, 'MFA_REQUIRED') : problem(401, 'INVALID_CREDENTIALS')))
    const { user } = await renderApp('/entrar')
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    await user.type(await screen.findByLabelText('Código do autenticador'), '654321')

    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('E-mail ou senha incorretos.')
    expect(screen.getByLabelText('Código do autenticador')).toHaveValue('654321')
  })

  it('o campo do código recebe o foco quando aparece', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => problem(401, 'MFA_REQUIRED'))
    const { user } = await renderApp('/entrar')

    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')

    expect(await screen.findByLabelText('Código do autenticador')).toHaveFocus()
  })

  it('entra com um código de recuperação, sem mandar o TOTP e sem guardá-lo no MutationCache', async () => {
    useAntiforgeryEndpoint()
    let attempt = 0
    const received = useLoginEndpoint(() =>
      ++attempt === 1 ? problem(401, 'MFA_REQUIRED') : new HttpResponse(null, { status: 204 }),
    )
    useAccountEndpoints()
    const queryClient = createTestQueryClient()
    const { user } = await renderApp('/entrar', queryClient)
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    await user.type(await screen.findByLabelText('Código do autenticador'), '12')

    await user.click(screen.getByRole('button', { name: 'Usar um código de recuperação' }))
    const field = screen.getByLabelText('Código de recuperação')
    expect(field).toHaveFocus()
    expect(screen.queryByLabelText('Código do autenticador')).not.toBeInTheDocument()
    await user.type(field, 'synthetic-recovery-1')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(
      await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(received[1]?.body).toEqual({
      email: 'admin@exemplo.com',
      password: 'frase longa de senha',
      recoveryCode: 'synthetic-recovery-1',
    })
    const cached = JSON.stringify(
      queryClient
        .getMutationCache()
        .getAll()
        .map((mutation) => mutation.state),
    )
    expect(cached).not.toContain('synthetic-recovery-1')
  })

  it('volta ao código do autenticador', async () => {
    useAntiforgeryEndpoint()
    useLoginEndpoint(() => problem(401, 'MFA_REQUIRED'))
    const { user } = await renderApp('/entrar')
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    await user.click(await screen.findByRole('button', { name: 'Usar um código de recuperação' }))

    await user.click(screen.getByRole('button', { name: 'Usar o código do autenticador' }))

    expect(screen.getByLabelText('Código do autenticador')).toHaveFocus()
    expect(screen.queryByLabelText('Código de recuperação')).not.toBeInTheDocument()
  })

  it('código de recuperação vazio é recusado sem chamar a API de novo', async () => {
    useAntiforgeryEndpoint()
    const received = useLoginEndpoint(() => problem(401, 'MFA_REQUIRED'))
    const { user } = await renderApp('/entrar')
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    await user.click(await screen.findByRole('button', { name: 'Usar um código de recuperação' }))

    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(screen.getByLabelText('Código de recuperação')).toHaveAccessibleDescription(
      'Informe um dos seus códigos de recuperação.',
    )
    expect(received).toHaveLength(1)
  })

  it('senha e código não ficam nas variables do MutationCache', async () => {
    useAntiforgeryEndpoint()
    let attempt = 0
    useLoginEndpoint(() => (++attempt === 1 ? problem(401, 'MFA_REQUIRED') : new HttpResponse(null, { status: 204 })))
    useAccountEndpoints()
    const queryClient = createTestQueryClient()
    const states: string[] = []
    queryClient.getMutationCache().subscribe(() => {
      states.push(
        JSON.stringify(
          queryClient
            .getMutationCache()
            .getAll()
            .map((mutation) => mutation.state),
        ),
      )
    })
    const { user } = await renderApp('/entrar', queryClient)
    await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
    await user.type(await screen.findByLabelText('Código do autenticador'), '123456')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))
    await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS })

    expect(states.length).toBeGreaterThan(0)
    for (const secret of ['frase longa de senha', '123456']) expect(states.join('\n')).not.toContain(secret)
  })
})

it('aceita o TOTP com espaços e envia só os dígitos', async () => {
  useAntiforgeryEndpoint()
  let attempt = 0
  const received = useLoginEndpoint(() =>
    ++attempt === 1 ? problem(401, 'MFA_REQUIRED') : new HttpResponse(null, { status: 204 }),
  )
  useAccountEndpoints()
  const { user } = await renderApp('/entrar')
  await fillAndSubmit(user, 'admin@exemplo.com', 'frase longa de senha')
  await user.type(await screen.findByLabelText('Código do autenticador'), ' 123 456 ')
  await user.click(screen.getByRole('button', { name: 'Entrar' }))

  expect(
    await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
  ).toBeInTheDocument()
  expect(received[1]?.body).toMatchObject({ twoFactorCode: '123456' })
})
