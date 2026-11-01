import { act, screen, waitFor, within } from '@testing-library/react'
import { delay, http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { apiClient } from '@/shared/api'
import { API, problem, server } from '@/test/msw'
import {
  TEST_PRIVATE_PROOF,
  TEST_RENEWED_SESSION_ID,
  TEST_SESSION_ID,
  withSessionContext,
} from '@/test/private-context'
import { createTestQueryClient, renderApp } from '@/test/render'

const PARTICIPANT = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'mfa@example.test',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: true,
  roles: ['participant'],
}

function scenario({ enabled = true, roles = ['participant'], status = 'Active' } = {}) {
  const state = {
    enabled,
    roles,
    sid: TEST_SESSION_ID,
    deleted: 0,
    requests: [] as { proof: string | null; body: string }[],
  }
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(
        withSessionContext({ ...PARTICIPANT, roles: state.roles, status, mfaEnabled: state.enabled }, state.sid),
      ),
    ),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    http.delete(`${API}/me/mfa`, async ({ request }) => {
      state.requests.push({ proof: request.headers.get('X-XSRF-TOKEN'), body: await request.text() })
      state.deleted++
      state.enabled = false
      state.sid = TEST_RENEWED_SESSION_ID
      return new HttpResponse(null, { status: 204 })
    }),
  )
  return state
}

async function openConfirmation(user: Awaited<ReturnType<typeof renderApp>>['user']) {
  await user.click(await screen.findByRole('button', { name: 'Desativar 2FA' }))
  return screen.getByRole('group', { name: 'Desativar a autenticação em duas etapas?' })
}

async function disabledNotice(text = 'Autenticação em duas etapas desativada.') {
  const message = await screen.findByText(text)
  const notice = message.closest('[role="status"]')
  expect(notice).not.toBeNull()
  return notice as HTMLElement
}

describe('escolha de MFA do participante', () => {
  it('permite continuar sem MFA e alcançar o cadastro voluntariamente pela conta', async () => {
    scenario({ enabled: false })
    const { user } = await renderApp('/account')
    expect(await screen.findByText('Desativada')).toBeInTheDocument()
    expect(screen.getByText('Para sua conta, a autenticação em duas etapas é opcional.')).toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Ativar 2FA' }))
    expect(await screen.findByRole('button', { name: 'Configurar autenticador' })).toBeInTheDocument()
    await user.click(screen.getByRole('link', { name: 'Voltar para a conta' }))
    expect(await screen.findByRole('link', { name: 'Ativar 2FA' })).toBeInTheDocument()
  })

  it('cancelar a confirmação não desativa e devolve o foco ao controle original', async () => {
    const state = scenario()
    const { user } = await renderApp('/account')
    expect(await openConfirmation(user)).toHaveFocus()
    expect(screen.getByText(/códigos de recuperação.*deixarão de funcionar/i)).toBeInTheDocument()
    expect(state.deleted).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Manter 2FA' }))
    expect(screen.queryByRole('group', { name: 'Desativar a autenticação em duas etapas?' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Desativar 2FA' })).toHaveFocus()
    expect(state.deleted).toBe(0)
  })

  it('confirma a desativação com prova privada e só mostra sucesso depois do bootstrap da sessão renovada', async () => {
    const state = scenario()
    const { user, queryClient } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    expect(await disabledNotice()).toHaveTextContent('Autenticação em duas etapas desativada.')
    expect(await disabledNotice()).toHaveFocus()
    expect(screen.getByRole('link', { name: 'Ativar 2FA' })).toBeInTheDocument()
    expect(state.deleted).toBe(1)
    expect(state.requests).toEqual([{ proof: TEST_PRIVATE_PROOF, body: '' }])
    expect(
      JSON.stringify(
        queryClient
          .getMutationCache()
          .getAll()
          .map((m) => m.state),
      ),
    ).not.toContain('mfa-disabled')
  })

  it('aguarda o servidor confirmar a identidade antes de anunciar a desativação', async () => {
    const state = scenario()
    let release!: () => void
    const pending = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.get(`${API}/me`, async () => {
        if (!state.enabled) await pending
        return HttpResponse.json(withSessionContext({ ...PARTICIPANT, mfaEnabled: state.enabled }, state.sid))
      }),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    expect(await screen.findByText('Carregando sua conta…')).toBeInTheDocument()
    expect(screen.queryByText('Autenticação em duas etapas desativada.')).not.toBeInTheDocument()
    release()
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Autenticação em duas etapas desativada.'))
  })

  it.each(['outra-identidade', 'mfa-continua-ativo'])('não aceita confirmação incoerente: %s', async (variant) => {
    const state = scenario()
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(
          withSessionContext(
            {
              ...PARTICIPANT,
              id:
                !state.enabled && variant === 'outra-identidade'
                  ? '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
                  : PARTICIPANT.id,
              email: !state.enabled && variant === 'outra-identidade' ? 'other@example.test' : PARTICIPANT.email,
              mfaEnabled: !state.enabled && variant === 'mfa-continua-ativo' ? true : state.enabled,
            },
            state.sid,
          ),
        ),
      ),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await waitFor(() => {
      expect(apiClient.sessionContext.getSnapshot().protected).toBe(true)
    })
    expect(screen.queryByText('Autenticação em duas etapas desativada.')).not.toBeInTheDocument()
    expect(screen.queryByText('other@example.test')).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Ativar 2FA' })).not.toBeInTheDocument()
  })

  it('a resposta atrasada não age pela identidade que entrou em outra aba', async () => {
    scenario()
    let requested!: () => void, release!: () => void
    const started = new Promise<void>((resolve) => {
      requested = resolve
    })
    const pending = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.delete(`${API}/me/mfa`, async () => {
        requested()
        await pending
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await started
    act(() => {
      apiClient.sessionContext.protect('identity')
    })
    release()
    await delay(10)
    expect(screen.queryByText('Autenticação em duas etapas desativada.')).not.toBeInTheDocument()
    expect(apiClient.sessionContext.getSnapshot().protected).toBe(true)
  })

  it.each([
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar sua identidade'],
    [403, 'MFA_REQUIRED_BY_ROLE', 'A autenticação em duas etapas é obrigatória para sua conta.'],
    [429, 'RATE_LIMITED', 'Muitas tentativas seguidas'],
  ])('mantém MFA ativo e explica a recusa %s/%s', async (status, code, text) => {
    scenario()
    let attempts = 0
    server.use(
      http.delete(`${API}/me/mfa`, () => {
        attempts++
        return problem(status, code)
      }),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(text)
    expect(screen.queryByText('Autenticação em duas etapas desativada.')).not.toBeInTheDocument()
    expect(attempts).toBe(1)
  })

  it('mantém os controles de segurança da própria conta suspensa', async () => {
    scenario({ status: 'Suspended' })
    const { user } = await renderApp('/account/suspended')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await waitFor(() =>
      expect(
        within(screen.getByRole('region', { name: 'Autenticação em duas etapas' })).getByRole('status'),
      ).toHaveTextContent('Autenticação em duas etapas desativada.'),
    )
    expect(screen.getByRole('heading', { name: 'Conta suspensa' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ativar 2FA' })).toBeInTheDocument()
  })

  it('protege o contexto inteiro quando a prova de CSRF é recusada', async () => {
    scenario()
    server.use(http.delete(`${API}/me/mfa`, () => problem(403, 'CSRF_VALIDATION_FAILED')))
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await waitFor(() => {
      expect(apiClient.sessionContext.getSnapshot().protected).toBe(true)
    })
    expect(screen.queryByText(PARTICIPANT.email)).not.toBeInTheDocument()
    expect(screen.queryByText('Autenticação em duas etapas desativada.')).not.toBeInTheDocument()
  })

  it('pede novo login quando a autenticação recente expirou', async () => {
    scenario()
    server.use(
      http.delete(`${API}/me/mfa`, () => problem(401, 'REAUTHENTICATION_REQUIRED')),
      http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await user.click(await screen.findByRole('button', { name: 'Entrar novamente' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Entrar' })).toBeInTheDocument()
    expect(screen.queryByText(PARTICIPANT.email)).not.toBeInTheDocument()
  })

  it('não envia a desativação duas vezes enquanto aguarda a primeira resposta', async () => {
    scenario()
    let attempts = 0,
      release!: () => void
    const pending = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.delete(`${API}/me/mfa`, async () => {
        attempts++
        await pending
        return problem(503, 'TEMPORARILY_UNAVAILABLE')
      }),
    )
    const { user } = await renderApp('/account')
    await openConfirmation(user)
    await user.click(screen.getByRole('button', { name: 'Confirmar desativação' }))
    await user.click(screen.getByRole('button', { name: 'Desativando…' }))
    expect(attempts).toBe(1)
    release()
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })

  it('em inglês oferece a escolha e confirma a desativação', async () => {
    scenario()
    const { user } = await renderApp('/account', createTestQueryClient(), 'en-US')
    await user.click(await screen.findByRole('button', { name: 'Turn off 2FA' }))
    await user.click(screen.getByRole('button', { name: 'Confirm turning off 2FA' }))
    expect(await disabledNotice('Two-factor authentication turned off.')).toHaveTextContent(
      'Two-factor authentication turned off.',
    )
    expect(screen.getByRole('link', { name: 'Turn on 2FA' })).toBeInTheDocument()
  })
})

describe('MFA obrigatório preservado', () => {
  it.each([['operator'], ['product_admin'], ['moderator'], ['participant', 'moderator'], ['future-role'], []])(
    'não oferece desativação para papéis %j',
    async (...roles: string[]) => {
      scenario({ roles })
      await renderApp('/account')
      expect(await screen.findByText('A autenticação em duas etapas é obrigatória para sua conta.')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Desativar/ })).not.toBeInTheDocument()
      expect(screen.queryByRole('link', { name: 'Ativar 2FA' })).not.toBeInTheDocument()
    },
  )

  it('conta privilegiada sem MFA continua obrigada a concluir o cadastro', async () => {
    scenario({ enabled: false, roles: ['moderator'] })
    await renderApp('/account')
    expect(await screen.findByRole('button', { name: 'Configurar autenticador' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Voltar para a conta' })).not.toBeInTheDocument()
    expect(screen.queryByText('Sua conta')).not.toBeInTheDocument()
  })
})
