import {
  withSessionContext,
  passwordChangeProblem,
  TEST_PRIVATE_PROOF,
  TEST_SESSION_ID,
  TEST_RENEWED_SESSION_ID,
} from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, renderApp, ROUTE_CHUNK_TIMEOUT_MS, type RenderedApp } from '@/test/render'

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
  server.use(http.get(`${API}/me`, () => passwordChangeProblem()))
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
  it.each(['/account', '/account/profile', '/account/enable-mfa', '/change-initial-password'])(
    'restringe a navegação: %s',
    async (path) => {
      restricted()
      await renderApp(path)
      expect(await screen.findByLabelText('Senha temporária', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveAttribute(
        'autocomplete',
        'current-password',
      )
      expect(screen.getByTestId('location')).toHaveTextContent('/change-initial-password')
    },
  )
  it('valida confirmação antes de enviar', async () => {
    restricted()
    const { user } = await renderApp('/account')
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
          ? passwordChangeProblem()
          : HttpResponse.json(
              withSessionContext(
                { ...ME, mfaEnabled: phase === 'done' },
                phase === 'done' ? TEST_RENEWED_SESSION_ID : TEST_SESSION_ID,
              ),
            ),
      ),
      http.post(`${API}/me/password-changes`, async ({ request }) => {
        sent = await request.json()
        sentCsrf = request.headers.get('X-XSRF-TOKEN')
        phase = 'mfa'
        return new HttpResponse(null, { status: 204 })
      }),
      http.post(`${API}/me/mfa/enrollments`, ({ request }) => {
        expect(request.headers.get('X-XSRF-TOKEN')).toBe(TEST_PRIVATE_PROOF)
        return HttpResponse.json({ sharedKey: 'SYNTHETICKEY', authenticatorUri: 'otpauth://totp/test' })
      }),
      http.post(`${API}/me/mfa/confirmations`, async ({ request }) => {
        expect(await request.json()).toEqual({ code: '123456' })
        expect(request.headers.get('X-XSRF-TOKEN')).toBe(TEST_PRIVATE_PROOF)
        phase = 'done'
        return HttpResponse.json({ resultSessionId: TEST_RENEWED_SESSION_ID, recoveryCodes: ['synthetic-recovery'] })
      }),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    )
    const queryClient = createTestQueryClient()
    // Assina o cache desde o início: com gcTime 0 cada mutação concluída sai dele, e olhar só no fim não veria a
    // senha nas variables da troca, que terminou duas telas antes.
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
    const { user } = await renderApp('/login', queryClient)
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
    expect(sentCsrf).toBe(TEST_PRIVATE_PROOF)
    expect(csrf.calls()).toBe(1)
    await user.click(screen.getByRole('button', { name: 'Configurar autenticador' }))
    expect(await screen.findByText('SYNTHETICKEY')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Código do autenticador'), '123456')
    await user.click(screen.getByRole('button', { name: 'Ativar MFA' }))
    expect(await screen.findByText('synthetic-recovery')).toBeInTheDocument()
    expect(states.length).toBeGreaterThan(0)
    const cached = states.join('\n')
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
    const { user } = await renderApp('/account')
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
    const { user } = await renderApp('/account')
    await fill(user)
    expect(await screen.findByRole('alert')).toHaveTextContent('diferente da senha temporária')
  })
  it('sessão expirada vai à entrada', async () => {
    server.use(http.get(`${API}/me`, () => problem(401, 'UNAUTHENTICATED')))
    await renderApp('/change-initial-password')
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/login')
    })
  })
  it('participante normal não fica preso na troca', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext({ ...ME, roles: ['participant'] }))),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    )
    await renderApp('/change-initial-password')
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
  const { user } = await renderApp('/account')
  await fill(user)
  expect(await screen.findByRole('alert')).toHaveTextContent('conexão')
  expect(calls).toBe(1)
})

describe('entrar novamente encerra a sessão restrita', () => {
  it.each([
    ['troca de senha', '/change-initial-password', () => passwordChangeProblem()],
    ['cadastro de MFA', '/account/enable-mfa', () => HttpResponse.json(withSessionContext(ME))],
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
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/login$/)
    })
    expect(logouts).toHaveLength(1)
    expect(logouts[0]).toBe(TEST_PRIVATE_PROOF)
    expect(await screen.findByRole('status', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(
      'Você saiu da sua conta.',
    )
  })

  it('falha de rede no logout fica na tela e avisa', async () => {
    restricted()
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => HttpResponse.error()))
    const { user } = await renderApp('/change-initial-password')

    await user.click(
      await screen.findByRole('button', { name: 'Entrar novamente' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent('conexão')
    expect(screen.getByTestId('location')).toHaveTextContent('/change-initial-password')
  })
})

it('CSRF da troca protege antes de novo envio e não busca token público', async () => {
  restricted()
  const csrf = useAntiforgeryEndpoint()
  let calls = 0
  server.use(
    http.post(`${API}/me/password-changes`, () => {
      calls += 1
      return problem(403, 'CSRF_VALIDATION_FAILED')
    }),
  )
  const { user } = await renderApp('/account')
  await fill(user)
  await screen.findByRole('button', { name: 'Tentar de novo' })
  expect(screen.queryByLabelText('Senha temporária')).not.toBeInTheDocument()
  expect(screen.getByText(/Não foi possível validar o contexto/)).toBeInTheDocument()
  expect(calls).toBe(1)
  expect(csrf.calls()).toBe(0)
})
