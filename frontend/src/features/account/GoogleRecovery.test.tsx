import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server } from '@/test/msw'
import {
  TEST_PRIVATE_PROOF,
  TEST_RENEWED_SESSION_ID,
  TEST_SESSION_ID,
  withSessionContext,
} from '@/test/private-context'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'

interface RecoveryScenario {
  readonly enabled?: boolean
  readonly linkingEnabled?: boolean
  readonly status?: 'Active' | 'Suspended'
  readonly roles?: readonly string[]
  readonly mfaEnabled?: boolean
  readonly requireReauthentication?: boolean
}

function recoveryScenario(options: RecoveryScenario = {}) {
  const state = {
    linked: true,
    sessionId: TEST_SESSION_ID,
    reauthenticated: options.requireReauthentication !== true,
    listProofs: [] as (string | null)[],
    removalProofs: [] as (string | null)[],
    reauthenticationRequests: [] as { readonly body: unknown; readonly proof: string | null }[],
    linkRequests: 0,
  }
  const account = {
    id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
    email: 'synthetic-google-recovery@example.test',
    status: options.status ?? 'Active',
    emailConfirmed: true,
    mfaEnabled: options.mfaEnabled ?? false,
    roles: options.roles ?? ['participant'],
    ...(options.status === 'Suspended'
      ? { suspension: { reasonCode: 'TermsViolation', suspendedAt: '2026-10-30T12:00:00Z' } }
      : {}),
  }
  server.use(
    http.get(`${API}/auth/google/options`, () =>
      HttpResponse.json({
        enabled: options.enabled ?? true,
        registrationEnabled: false,
        linkingEnabled: options.linkingEnabled ?? true,
      }),
    ),
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(account, state.sessionId))),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    http.get(`${API}/me/external-logins`, ({ request }) => {
      state.listProofs.push(request.headers.get('X-XSRF-TOKEN'))
      return HttpResponse.json({
        items: state.linked ? [{ provider: 'Google', linkedAt: '2026-10-01T00:00:00Z' }] : [],
      })
    }),
    http.delete(`${API}/me/external-logins/google`, ({ request }) => {
      state.removalProofs.push(request.headers.get('X-XSRF-TOKEN'))
      if (!state.reauthenticated) return problem(401, 'REAUTHENTICATION_REQUIRED')
      state.linked = false
      state.sessionId = TEST_RENEWED_SESSION_ID
      return new HttpResponse(null, { status: 204 })
    }),
    http.post(`${API}/auth/reauthenticate`, async ({ request }) => {
      state.reauthenticationRequests.push({
        body: await request.json(),
        proof: request.headers.get('X-XSRF-TOKEN'),
      })
      state.reauthenticated = true
      return new HttpResponse(null, { status: 204 })
    }),
    http.post(`${API}/me/external-logins/google/requests`, () => {
      state.linkRequests++
      return problem(403, 'FORBIDDEN')
    }),
  )
  return state
}

describe('local recovery of an existing Google login', () => {
  it.each([
    { enabled: true, linkingEnabled: false },
    { enabled: false, linkingEnabled: false },
  ])('lists and removes a link with provider capability flags %j', async (capability) => {
    const state = recoveryScenario(capability)
    const { user } = await renderApp('/account')

    await user.click(await screen.findByRole('button', { name: 'Remover Google' }))
    expect(state.listProofs).toContain(TEST_PRIVATE_PROOF)
    expect(state.removalProofs).toEqual([])
    expect(screen.queryByRole('button', { name: 'Vincular Google' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))

    expect(
      await screen.findByText('Vínculo com Google removido. Sua senha do Duora continua funcionando.'),
    ).toBeInTheDocument()
    expect(state.removalProofs).toEqual([TEST_PRIVATE_PROOF])
    expect(state.linked).toBe(false)
    expect(state.sessionId).toBe(TEST_RENEWED_SESSION_ID)
    expect(screen.queryByRole('button', { name: 'Vincular Google' })).not.toBeInTheDocument()
    expect(state.linkRequests).toBe(0)
  })

  it('offers local Google removal after the account route redirects a suspended owner', async () => {
    const state = recoveryScenario({ status: 'Suspended' })
    const { user } = await renderApp('/account')

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Conta suspensa' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('/account/suspended')
    await user.click(await screen.findByRole('button', { name: 'Remover Google' }))
    expect(state.listProofs).toContain(TEST_PRIVATE_PROOF)
    expect(state.removalProofs).toEqual([])
    await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))

    expect(
      await screen.findByText('Vínculo com Google removido. Sua senha do Duora continua funcionando.'),
    ).toBeInTheDocument()
    expect(state.removalProofs).toEqual([TEST_PRIVATE_PROOF])
    expect(screen.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Vincular Google' })).not.toBeInTheDocument()
    expect(state.linkRequests).toBe(0)
  })

  it('lets an owner who became privileged remove an existing link after local password and MFA step-up', async () => {
    const state = recoveryScenario({ roles: ['moderator'], mfaEnabled: true, requireReauthentication: true })
    const { user, queryClient } = await renderApp('/account')
    const snapshots: string[] = []
    queryClient.getMutationCache().subscribe(() => {
      snapshots.push(
        JSON.stringify(
          queryClient
            .getMutationCache()
            .getAll()
            .map((mutation) => mutation.state),
        ),
      )
    })

    await user.click(await screen.findByRole('button', { name: 'Remover Google' }))
    expect(state.listProofs).toContain(TEST_PRIVATE_PROOF)
    expect(screen.queryByRole('button', { name: 'Vincular Google' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))
    await user.type(await screen.findByLabelText('Senha do Duora'), 'synthetic privileged recovery password')
    await user.type(screen.getByLabelText('Código do autenticador'), '123456')
    await user.click(screen.getByRole('button', { name: 'Confirmar identidade' }))

    expect(await screen.findByText('Identidade confirmada. Confirme a ação novamente.')).toBeInTheDocument()
    expect(state.reauthenticationRequests).toEqual([
      {
        body: { password: 'synthetic privileged recovery password', twoFactorCode: '123456' },
        proof: TEST_PRIVATE_PROOF,
      },
    ])
    expect(state.removalProofs).toEqual([TEST_PRIVATE_PROOF])
    await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))

    expect(
      await screen.findByText('Vínculo com Google removido. Sua senha do Duora continua funcionando.'),
    ).toBeInTheDocument()
    expect(state.removalProofs).toEqual([TEST_PRIVATE_PROOF, TEST_PRIVATE_PROOF])
    expect(screen.queryByRole('button', { name: 'Vincular Google' })).not.toBeInTheDocument()
    expect(state.linkRequests).toBe(0)
    expect(snapshots.join('\n')).not.toContain('synthetic privileged recovery password')
    expect(snapshots.join('\n')).not.toContain('123456')
  })
})
