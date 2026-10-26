import {
  withSessionContext,
  passwordChangeProblem,
  TEST_PRIVATE_PROOF,
  TEST_SESSION_ID,
  TEST_RENEWED_SESSION_ID,
} from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { delay, http, HttpResponse } from 'msw'
import { beforeEach, describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS, type RenderedApp } from '@/test/render'

const ADMIN_WITHOUT_MFA = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'admin@example.test',
  status: 'Active',
  emailConfirmed: true,
  roles: ['operator'],
  mfaEnabled: false,
}
const SHARED_KEY = 'SYNTHETICKEY'
const RECHECK_LATENCY_MS = 50

/**
 * Fase do MFA como o servidor a expõe: /me está na allowlist do cadastro (AllowDuringMfaEnrollment) e responde 200
 * com mfaEnabled false e o papel privilegiado; as rotas fora do cadastro respondem 403 MFA_ENROLLMENT_REQUIRED.
 */
let mfaState = { enabled: false, sid: TEST_SESSION_ID }
beforeEach(() => {
  mfaState = { enabled: false, sid: TEST_SESSION_ID }
})
function mfaPhase() {
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(withSessionContext({ ...ADMIN_WITHOUT_MFA, mfaEnabled: mfaState.enabled }, mfaState.sid)),
    ),
    http.get(`${API}/me/profile`, () => problem(403, 'MFA_ENROLLMENT_REQUIRED')),
    http.get(`${API}/me/sessions`, () => problem(403, 'MFA_ENROLLMENT_REQUIRED')),
  )
}

function useEnrollmentEndpoint(
  respond: () => Response = () => HttpResponse.json({ sharedKey: SHARED_KEY, authenticatorUri: 'otpauth://totp/test' }),
) {
  let calls = 0
  server.use(
    http.post(`${API}/me/mfa/enrollments`, ({ request }) => {
      expect(request.headers.get('X-XSRF-TOKEN')).toBe(TEST_PRIVATE_PROOF)
      calls++
      return respond()
    }),
  )
  return { calls: () => calls }
}

function useConfirmationEndpoint(respond: () => Response) {
  let calls = 0
  server.use(
    http.post(`${API}/me/mfa/confirmations`, ({ request }) => {
      expect(request.headers.get('X-XSRF-TOKEN')).toBe(TEST_PRIVATE_PROOF)
      calls++
      const response = respond()
      if (response.ok) {
        mfaState.enabled = true
        mfaState.sid = TEST_RENEWED_SESSION_ID
      }
      return response
    }),
  )
  return { calls: () => calls }
}

async function startEnrollment(user: RenderedApp['user']) {
  await user.click(
    await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
  )
  expect(await screen.findByText(SHARED_KEY, {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toBeInTheDocument()
}

async function submitCode(user: RenderedApp['user'], code: string) {
  await user.type(screen.getByLabelText('Código do autenticador'), code)
  await user.click(screen.getByRole('button', { name: 'Ativar MFA' }))
}

describe('fase do cadastro de MFA', () => {
  it.each(['/account', '/account/profile', '/change-initial-password', '/account/enable-mfa'])(
    'conta privilegiada sem MFA vai ao cadastro: %s',
    async (path) => {
      mfaPhase()
      await renderApp(path)
      expect(
        await screen.findByRole(
          'heading',
          { name: 'Ative a autenticação em duas etapas' },
          { timeout: ROUTE_CHUNK_TIMEOUT_MS },
        ),
      ).toBeInTheDocument()
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/account\/enable-mfa$/)
    },
  )

  it('403 sem contexto canônico não libera a árvore de MFA', async () => {
    server.use(http.get(`${API}/me`, () => problem(403, 'MFA_ENROLLMENT_REQUIRED')))
    await renderApp('/account/profile')
    await screen.findByRole('button', { name: 'Tentar de novo' })
    expect(screen.queryByRole('button', { name: 'Configurar autenticador' })).not.toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/account\/profile$/)
  })

  it.each(['12345', '1234567', 'abcdef', '12 456'])('recusa o código %s sem chamar a API', async (code) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() =>
      HttpResponse.json({ resultSessionId: TEST_RENEWED_SESSION_ID, recoveryCodes: ['x'] }),
    )
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, code)

    expect(screen.getByLabelText('Código do autenticador')).toHaveAccessibleDescription(
      'Informe um código válido de 6 dígitos do autenticador.',
    )
    // O erro não é região viva: sem o foco no campo, o leitor de tela não diria nada depois do envio.
    expect(screen.getByLabelText('Código do autenticador')).toHaveFocus()
    expect(confirmation.calls()).toBe(0)
  })

  it('aceita o código com espaços, como o servidor, e envia só os dígitos', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const bodies: unknown[] = []
    server.use(
      http.post(`${API}/me/mfa/confirmations`, async ({ request }) => {
        bodies.push(await request.json())
        mfaState.enabled = true
        mfaState.sid = TEST_RENEWED_SESSION_ID
        return HttpResponse.json({ resultSessionId: TEST_RENEWED_SESSION_ID, recoveryCodes: ['synthetic-recovery'] })
      }),
    )
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, '123 456')

    expect(await screen.findByText('synthetic-recovery', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toBeInTheDocument()
    expect(bodies).toEqual([{ code: '123456' }])
  })

  it.each([
    [400, 'MFA_CODE_INVALID', 'Informe um código válido de 6 dígitos'],
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar sua identidade'],
    [409, 'MFA_ALREADY_ENABLED', 'O MFA já está ativo'],
    [409, 'MFA_ENROLLMENT_NOT_STARTED', 'A configuração expirou'],
  ])('confirmação com %s %s mostra a orientação, sem retry', async (status, code, message) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() => problem(status, code))
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, '123456')

    expect(await screen.findByRole('alert', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(message)
    expect(confirmation.calls()).toBe(1)
  })

  it.each([
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar sua identidade'],
    [409, 'MFA_ALREADY_ENABLED', 'O MFA já está ativo'],
  ])('início do cadastro com %s %s mostra a orientação', async (status, code, message) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    const enrollment = useEnrollmentEndpoint(() => problem(status, code))
    const { user } = await renderApp('/account/enable-mfa')

    await user.click(
      await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByRole('alert', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(message)
    expect(screen.queryByText(SHARED_KEY)).not.toBeInTheDocument()
    expect(enrollment.calls()).toBe(1)
  })
})

describe('limite de tentativas', () => {
  it('início do cadastro com 429 mostra a espera do Retry-After, sem retry', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    const enrollment = useEnrollmentEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '30' }))
    const { user } = await renderApp('/account/enable-mfa')

    await user.click(
      await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByRole('alert', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(
      'espere 30 segundos',
    )
    expect(enrollment.calls()).toBe(1)
  })

  it('confirmação com 429 mostra a espera do Retry-After e mantém a chave, sem retry', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() => problem(429, 'RATE_LIMITED', { 'Retry-After': '120' }))
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, '123456')

    expect(await screen.findByRole('alert', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toHaveTextContent(
      'espere 2 minutos',
    )
    expect(screen.getByText(SHARED_KEY)).toBeInTheDocument()
    expect(confirmation.calls()).toBe(1)
  })
})

describe('foco ao trocar de estado', () => {
  it('a chave recebe o foco quando o botão de início some', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const { user } = await renderApp('/account/enable-mfa')

    await startEnrollment(user)

    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toContainElement(screen.getByText(SHARED_KEY))
  })

  it('os códigos de recuperação chegam com foco e anúncio', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    useConfirmationEndpoint(() =>
      HttpResponse.json({ resultSessionId: TEST_RENEWED_SESSION_ID, recoveryCodes: ['synthetic-recovery'] }),
    )
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, '123456')

    const announcement = await screen.findByText(
      'Autenticação em duas etapas ativada.',
      {},
      { timeout: ROUTE_CHUNK_TIMEOUT_MS },
    )
    expect(announcement.closest('[role="status"]')).toHaveFocus()
    expect(screen.getByText('synthetic-recovery')).toBeInTheDocument()
  })
})

describe('segredo do TOTP e códigos de recuperação', () => {
  function storageContents(storage: Storage): string {
    return JSON.stringify(
      Array.from({ length: storage.length }, (_, index) => {
        const key = storage.key(index) ?? ''
        return [key, storage.getItem(key)]
      }),
    )
  }

  /** Tudo o que sobrevive à tela ou vai a outro lugar: caches, armazenamento do navegador e URL. */
  function persistedTraces(queryClient: RenderedApp['queryClient']): string {
    return [
      JSON.stringify(
        queryClient
          .getQueryCache()
          .getAll()
          .map((query) => ({ key: query.queryKey, state: query.state })),
      ),
      JSON.stringify(
        queryClient
          .getMutationCache()
          .getAll()
          .map((mutation) => mutation.state),
      ),
      storageContents(localStorage),
      storageContents(sessionStorage),
      screen.getByTestId('location').textContent,
      window.location.href,
    ].join('\n')
  }

  it('não aparecem em QueryCache, MutationCache, localStorage, sessionStorage nem na URL', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    useConfirmationEndpoint(() =>
      HttpResponse.json({ resultSessionId: TEST_RENEWED_SESSION_ID, recoveryCodes: ['synthetic-recovery'] }),
    )
    const { user, queryClient } = await renderApp('/account/enable-mfa')

    await startEnrollment(user)
    expect(persistedTraces(queryClient)).not.toContain(SHARED_KEY)
    expect(persistedTraces(queryClient)).not.toContain('otpauth://')

    await submitCode(user, '123456')
    expect(await screen.findByText('synthetic-recovery', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toBeInTheDocument()
    const traces = persistedTraces(queryClient)
    for (const secret of [SHARED_KEY, 'otpauth://', 'synthetic-recovery', '123456'])
      expect(traces).not.toContain(secret)
  })
})

describe('textos da tela', () => {
  it('participante lê instruções neutras, sem "sua conta exige"', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(withSessionContext({ ...ADMIN_WITHOUT_MFA, roles: ['participant'] })),
      ),
    )
    await renderApp('/account/enable-mfa')

    await screen.findByRole(
      'heading',
      { name: 'Ative a autenticação em duas etapas' },
      { timeout: ROUTE_CHUNK_TIMEOUT_MS },
    )
    expect(screen.getByText(/aplicativo autenticador/)).not.toHaveTextContent(/exige/i)
  })

  it('o botão de início mostra que está carregando', async () => {
    mfaPhase()
    useAntiforgeryEndpoint()
    let release = (): void => undefined
    const pending = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.post(`${API}/me/mfa/enrollments`, async () => {
        await pending
        return HttpResponse.json({ sharedKey: SHARED_KEY, authenticatorUri: 'otpauth://totp/test' })
      }),
    )
    const { user } = await renderApp('/account/enable-mfa')

    await user.click(
      await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(
      await screen.findByRole('button', { name: 'Preparando…' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toHaveAttribute('aria-disabled', 'true')
    release()
    expect(await screen.findByText(SHARED_KEY, {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS })).toBeInTheDocument()
  })
})

describe('erro 401 nas fases restritas', () => {
  /**
   * /me da fase, contando as leituras: o 401 de uma escrita reconsulta a identidade (query-client.ts). A reconsulta
   * demora um pouco, como numa rede real, para a tela atravessar o estado de busca.
   */
  function countedMe(respond: () => Response) {
    let calls = 0
    server.use(
      http.get(`${API}/me`, async () => {
        calls++
        if (calls > 1) await delay(RECHECK_LATENCY_MS)
        return respond()
      }),
    )
    return { calls: () => calls }
  }

  it('o erro e a chave continuam depois da reconsulta de /me', async () => {
    const me = countedMe(() => HttpResponse.json(withSessionContext(ADMIN_WITHOUT_MFA)))
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    useConfirmationEndpoint(() => problem(401, 'REAUTHENTICATION_REQUIRED'))
    const { user, queryClient } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)

    await submitCode(user, '123456')
    await waitFor(() => {
      expect(me.calls()).toBe(2)
      expect(queryClient.isFetching()).toBe(0)
    })

    expect(screen.getByRole('alert')).toHaveTextContent('Entre novamente para confirmar sua identidade')
    expect(screen.getByText(SHARED_KEY)).toBeInTheDocument()
  })

  it('a troca de senha mantém a recusa autenticada sem reconsultar /me', async () => {
    const me = countedMe(() => passwordChangeProblem())
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/me/password-changes`, () => problem(401, 'INVALID_CREDENTIALS')))
    const { user, queryClient } = await renderApp('/change-initial-password')
    await user.type(await screen.findByLabelText('Senha temporária', {}, { timeout: ROUTE_CHUNK_TIMEOUT_MS }), 'X')
    await user.type(screen.getByLabelText('Senha nova'), ' minha senha própria longa ')
    await user.type(screen.getByLabelText('Confirme a nova senha'), ' minha senha própria longa ')
    await user.click(screen.getByRole('button', { name: 'Salvar senha nova' }))
    await waitFor(() => {
      expect(me.calls()).toBe(1)
      expect(queryClient.isFetching()).toBe(0)
    })

    expect(screen.getByRole('alert')).toHaveTextContent('Senha atual inválida')
  })
})

it('CSRF privado rejeitado protege a árvore, descarta a chave e exige gesto sem repetir a confirmação', async () => {
  mfaPhase()
  useEnrollmentEndpoint()
  const confirmation = useConfirmationEndpoint(() => problem(403, 'CSRF_VALIDATION_FAILED'))
  const { user } = await renderApp('/account/enable-mfa')
  await startEnrollment(user)
  await submitCode(user, '123456')
  await screen.findByRole('button', { name: 'Tentar de novo' })
  expect(screen.queryByText(SHARED_KEY)).not.toBeInTheDocument()
  expect(confirmation.calls()).toBe(1)
  expect(screen.getByText(/A ação pode ter sido concluída/)).toBeInTheDocument()
})

it('não exibe os códigos na árvore antiga enquanto confirma o novo SID e não põe segredos nos caches', async () => {
  mfaPhase()
  useEnrollmentEndpoint()
  useConfirmationEndpoint(() =>
    HttpResponse.json({ recoveryCodes: ['delayed-code-secret'], resultSessionId: TEST_RENEWED_SESSION_ID }),
  )
  let release!: () => void
  let entered!: () => void
  const gate = new Promise<void>((resolve) => {
    release = resolve
  })
  const started = new Promise<void>((resolve) => {
    entered = resolve
  })
  server.use(
    http.get(`${API}/me`, async () => {
      if (mfaState.enabled) {
        entered()
        await gate
      }
      return HttpResponse.json(withSessionContext({ ...ADMIN_WITHOUT_MFA, mfaEnabled: mfaState.enabled }, mfaState.sid))
    }),
  )
  const { user, queryClient } = await renderApp('/account/enable-mfa')
  const captured: string[] = []
  const readCache = () => {
    captured.push(
      JSON.stringify({
        queries: queryClient
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
        mutations: queryClient
          .getMutationCache()
          .getAll()
          .map((mutation) => mutation.state),
      }),
    )
  }
  const unsubQuery = queryClient.getQueryCache().subscribe(readCache)
  const unsubMutation = queryClient.getMutationCache().subscribe(readCache)
  await startEnrollment(user)
  await submitCode(user, '123456')
  await started
  expect(screen.queryByText('delayed-code-secret')).not.toBeInTheDocument()
  expect(screen.queryByText(SHARED_KEY)).not.toBeInTheDocument()
  release()
  expect(await screen.findByText('delayed-code-secret')).toBeInTheDocument()
  unsubQuery()
  unsubMutation()
  for (const secret of ['delayed-code-secret', SHARED_KEY, TEST_PRIVATE_PROOF, TEST_RENEWED_SESSION_ID])
    expect(captured.join('\n')).not.toContain(secret)
})

it.each(['wrong-user', 'wrong-session', 'missing-result-session', 'bootstrap-failed'] as const)(
  'descarta recibo%s com aviso, sem repetir confirmação',
  async (failure) => {
    mfaPhase()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() =>
      HttpResponse.json({
        recoveryCodes: ['never-publish-secret'],
        ...(failure === 'missing-result-session' ? {} : { resultSessionId: TEST_RENEWED_SESSION_ID }),
      }),
    )
    server.use(
      http.get(`${API}/me`, () => {
        if (mfaState.enabled && failure === 'bootstrap-failed') return problem(503, 'UNAVAILABLE')
        const account = {
          ...ADMIN_WITHOUT_MFA,
          mfaEnabled: mfaState.enabled,
          ...(mfaState.enabled && failure === 'wrong-user' ? { id: '00000000-0000-4000-8000-000000000002' } : {}),
        }
        return HttpResponse.json(
          withSessionContext(account, mfaState.enabled && failure === 'wrong-session' ? TEST_SESSION_ID : mfaState.sid),
        )
      }),
    )
    const { user } = await renderApp('/account/enable-mfa')
    await startEnrollment(user)
    await submitCode(user, '123456')
    await screen.findByText(/A ação pode ter sido concluída/)
    expect(screen.queryByText('never-publish-secret')).not.toBeInTheDocument()
    expect(screen.queryByText(SHARED_KEY)).not.toBeInTheDocument()
    expect(confirmation.calls()).toBe(1)
  },
)
