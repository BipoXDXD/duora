import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS, type RenderedApp } from '@/test/render'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'admin@example.test',
  status: 'Active',
  emailConfirmed: true,
  roles: ['product_admin'],
  mfaEnabled: false,
}
const TEMPORARY = 'ABCD-EFGH-IJKL-MNOP-QRST-UVWX'
const PASSWORD = ' minha senha própria longa '
function restricted() {
  server.use(http.get(`${API}/me`, () => problem(403, 'PASSWORD_CHANGE_REQUIRED')))
}
/**
 * Cola o valor, como um gerenciador de senhas: um evento de input por campo. Digitar tecla a tecla as senhas longas
 * daqui (mais de 80 teclas por formulário) re-renderizava a árvore inteira a cada tecla e, com a CPU disputada,
 * empurrava a jornada completa para além do prazo do Vitest. O que o teste verifica é o valor enviado, não a digitação.
 */
async function enter(user: RenderedApp['user'], field: HTMLElement, value: string) {
  await user.click(field)
  await user.paste(value)
}
async function fill(user: RenderedApp['user'], confirmation = PASSWORD) {
  await enter(
    user,
    await screen.findByLabelText('Senha temporária', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    TEMPORARY,
  )
  await enter(user, screen.getByLabelText('Senha nova'), PASSWORD)
  await enter(user, screen.getByLabelText('Confirme a nova senha'), confirmation)
  await user.click(screen.getByRole('button', { name: 'Salvar senha nova' }))
}

describe('troca obrigatória da senha inicial', () => {
  it.each(['/conta', '/conta/perfil', '/conta/ativar-mfa', '/trocar-senha-inicial'])(
    'restringe a navegação: %s',
    async (path) => {
      restricted()
      await renderApp(path)
      expect(await screen.findByLabelText('Senha temporária', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveAttribute(
        'autocomplete',
        'current-password',
      )
      expect(screen.getByTestId('location')).toHaveTextContent('/trocar-senha-inicial')
    },
  )
  it('valida confirmação antes de enviar', async () => {
    restricted()
    const { user } = await renderApp('/conta')
    await fill(user, 'outra senha longa')
    expect(await screen.findByText('As senhas não coincidem.')).toBeInTheDocument()
  })
  it('integra login, troca, CSRF novo, MFA e códigos sem segredos no cache', async () => {
    const csrf = useAntiforgeryEndpoint()
    let phase = 'password'
    let sent: unknown
    let sentCsrf: string | null = null
    server.use(
      http.post(`${API}/auth/login`, () => new HttpResponse(null, { status: 204 })),
      http.get(`${API}/me`, () =>
        phase === 'password'
          ? problem(403, 'PASSWORD_CHANGE_REQUIRED')
          : HttpResponse.json({ ...ME, mfaEnabled: phase === 'done' }),
      ),
      http.post(`${API}/me/password-changes`, async ({ request }) => {
        sent = await request.json()
        sentCsrf = request.headers.get('X-XSRF-TOKEN')
        phase = 'mfa'
        return new HttpResponse(null, { status: 204 })
      }),
      http.post(`${API}/me/mfa/enrollments`, ({ request }) => {
        expect(request.headers.get('X-XSRF-TOKEN')).toBe(csrf.latestToken())
        return HttpResponse.json({ sharedKey: 'SYNTHETICKEY', authenticatorUri: 'otpauth://totp/test' })
      }),
      http.post(`${API}/me/mfa/confirmations`, async ({ request }) => {
        expect(await request.json()).toEqual({ code: '123456' })
        expect(request.headers.get('X-XSRF-TOKEN')).toBe(csrf.latestToken())
        phase = 'done'
        return HttpResponse.json({ recoveryCodes: ['synthetic-recovery'] })
      }),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    )
    const { user, queryClient } = await renderApp('/entrar')
    await enter(user, screen.getByLabelText('E-mail'), ME.email)
    await enter(user, screen.getByLabelText('Senha'), TEMPORARY)
    await user.click(screen.getByRole('button', { name: 'Entrar' }))
    await fill(user)
    expect(
      await screen.findByRole(
        'heading',
        { name: 'Ative a autenticação em duas etapas' },
        { timeout: ROUTE_CHUNK_TIMEOUT_MS },
      ),
    ).toBeInTheDocument()
    expect(sent).toEqual({ currentPassword: TEMPORARY, newPassword: PASSWORD })
    expect(sentCsrf).toBe('token-3')
    expect(csrf.calls()).toBe(4)
    await user.click(screen.getByRole('button', { name: 'Configurar autenticador' }))
    expect(await screen.findByText('SYNTHETICKEY')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Código do autenticador'), '123456')
    await user.click(screen.getByRole('button', { name: 'Ativar MFA' }))
    expect(await screen.findByText('synthetic-recovery')).toBeInTheDocument()
    const cached = JSON.stringify(
      queryClient
        .getMutationCache()
        .getAll()
        .map((mutation) => mutation.state),
    )
    for (const secret of [PASSWORD, TEMPORARY, 'SYNTHETICKEY', 'synthetic-recovery', '123456'])
      expect(cached).not.toContain(secret)
    await user.click(screen.getByRole('button', { name: 'Guardei os códigos, continuar' }))
    expect(
      await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
  })
  it.each([
    [401, 'INVALID_CREDENTIALS', 'Senha atual inválida'],
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar'],
    [403, 'CSRF_VALIDATION_FAILED', 'Não foi possível validar'],
    [429, 'RATE_LIMITED', 'tentativas'],
  ])('trata %s %s sem retry', async (status, code, message) => {
    restricted()
    useAntiforgeryEndpoint()
    let calls = 0
    server.use(
      http.post(`${API}/me/password-changes`, () => {
        calls++
        return problem(status, code)
      }),
    )
    const { user } = await renderApp('/conta')
    await fill(user)
    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(calls).toBe(1)
  })
  it('traduz a recusa de reutilização', async () => {
    restricted()
    useAntiforgeryEndpoint()
    server.use(
      http.post(`${API}/me/password-changes`, () =>
        HttpResponse.json(
          { title: 'validation', errors: { newPassword: ['SAME_AS_TEMPORARY_PASSWORD'] } },
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )
    const { user } = await renderApp('/conta')
    await fill(user)
    expect(await screen.findByRole('alert')).toHaveTextContent('diferente da senha temporária')
  })
  it('sessão expirada vai à entrada', async () => {
    server.use(http.get(`${API}/me`, () => problem(401, 'UNAUTHENTICATED')))
    await renderApp('/trocar-senha-inicial')
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/entrar')
    })
  })
  it('participante normal não fica preso na troca', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json({ ...ME, roles: ['participant'] })),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    )
    await renderApp('/trocar-senha-inicial')
    expect(
      await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
  })
})

it('falha de rede mantém a troca disponível sem reenviar a senha', async () => {
  restricted()
  useAntiforgeryEndpoint()
  let calls = 0
  server.use(
    http.post(`${API}/me/password-changes`, () => {
      calls++
      return HttpResponse.error()
    }),
  )
  const { user } = await renderApp('/conta')
  await fill(user)
  expect(await screen.findByRole('alert')).toHaveTextContent('conexão')
  expect(calls).toBe(1)
})

describe('entrar novamente encerra a sessão restrita', () => {
  it.each([
    ['troca de senha', '/trocar-senha-inicial', () => problem(403, 'PASSWORD_CHANGE_REQUIRED')],
    ['cadastro de MFA', '/conta/ativar-mfa', () => problem(403, 'MFA_ENROLLMENT_REQUIRED')],
  ])('%s: chama o logout antes de ir à entrada', async (_fase, path, me) => {
    useAntiforgeryEndpoint()
    const logouts: (string | null)[] = []
    server.use(
      http.get(`${API}/me`, me),
      http.post(`${API}/auth/logout`, ({ request }) => {
        logouts.push(request.headers.get('X-XSRF-TOKEN'))
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = await renderApp(path)

    await user.click(
      await screen.findByRole('button', { name: 'Entrar novamente' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/entrar$/)
    })
    expect(logouts).toHaveLength(1)
    expect(logouts[0]).toMatch(/^token-\d+$/)
    expect(await screen.findByRole('status', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(
      'Você saiu da sua conta.',
    )
  })

  it('falha de rede no logout fica na tela e avisa', async () => {
    restricted()
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => HttpResponse.error()))
    const { user } = await renderApp('/trocar-senha-inicial')

    await user.click(
      await screen.findByRole('button', { name: 'Entrar novamente' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent('conexão')
    expect(screen.getByTestId('location')).toHaveTextContent('/trocar-senha-inicial')
  })
})
