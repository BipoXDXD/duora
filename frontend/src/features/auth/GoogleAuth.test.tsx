import { act, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'
import { TEST_SESSION_ID, TEST_RENEWED_SESSION_ID, withSessionContext, installTestMe } from '@/test/private-context'
import { apiClient } from '@/shared/api'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'synthetic@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
function enabled() {
  server.use(
    http.get(`${API}/auth/google/options`, () =>
      HttpResponse.json({
        enabled: true,
        registrationEnabled: true,
        linkingEnabled: true,
      }),
    ),
  )
}
function flow(nextStep: string, email?: string) {
  server.use(
    http.get(`${API}/auth/google/flow`, () =>
      HttpResponse.json({
        nextStep,
        expiresAt: new Date(Date.now() + 300000).toISOString(),
        ...(email ? { email } : {}),
      }),
    ),
  )
}
function account() {
  const state = { sid: TEST_SESSION_ID }
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(ME, state.sid))),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
  )
  return state
}

describe('Google authentication', () => {
  it('discards a delayed flow after logout and never reuses its email for the next account', async () => {
    enabled()
    const previousEmail = 'previous-google-proof@example.test'
    const nextEmail = 'next-google-proof@example.test'
    let releaseResponse: (response: Response) => void = () => undefined
    const delayedResponse = new Promise<Response>((resolve) => {
      releaseResponse = resolve
    })
    let previousRequestSignal: AbortSignal | undefined
    let flowReads = 0
    server.use(
      http.get(`${API}/auth/google/flow`, ({ request }) => {
        flowReads++
        if (flowReads === 1) {
          previousRequestSignal = request.signal
          return delayedResponse
        }
        return HttpResponse.json({
          nextStep: 'Register',
          email: nextEmail,
          expiresAt: new Date(Date.now() + 300000).toISOString(),
        })
      }),
    )
    const previous = await renderApp('/google/continue')
    await waitFor(() => {
      expect(previousRequestSignal).toBeDefined()
    })

    act(() => {
      // The identity boundary runs before a new account can own the same browser/cache.
      apiClient.sessionContext.protect('identity')
      previous.queryClient.clear()
      previous.unmount()
      installTestMe({
        ...ME,
        status: 'Active',
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7',
        email: nextEmail,
      })
    })
    expect(previousRequestSignal?.aborted).toBe(true)

    await renderApp('/google/continue', previous.queryClient)
    expect(await screen.findByDisplayValue(nextEmail)).toBeInTheDocument()
    await act(async () => {
      releaseResponse(
        HttpResponse.json({
          nextStep: 'Register',
          email: previousEmail,
          expiresAt: new Date(Date.now() + 300000).toISOString(),
        }),
      )
      await delayedResponse
    })

    expect(screen.getByDisplayValue(nextEmail)).toBeInTheDocument()
    expect(screen.queryByDisplayValue(previousEmail)).not.toBeInTheDocument()
    expect(screen.queryByText(previousEmail)).not.toBeInTheDocument()
    expect(
      JSON.stringify(
        previous.queryClient
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toContain(previousEmail)
  })
  it('keeps local login and hides Google while disabled', async () => {
    await renderApp('/login')
    expect(screen.getByLabelText('Senha')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Continuar com Google' })).not.toBeInTheDocument()
  })
  it('offers Google when enabled and rejects an authorization URL outside the internal challenge', async () => {
    enabled()
    useAntiforgeryEndpoint()
    const received: unknown[] = []
    server.use(
      http.post(`${API}/auth/google/requests`, async ({ request }) => {
        received.push({ body: await request.json(), csrf: request.headers.get('X-XSRF-TOKEN') })
        return HttpResponse.json({ authorizationUrl: 'https://untrusted.example/authorize' })
      }),
    )
    const { user } = await renderApp('/login')
    await user.click(await screen.findByRole('button', { name: 'Continuar com Google' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(received).toEqual([{ body: { purpose: 'Login' }, csrf: 'token-1' }])
    expect(screen.getByLabelText('Senha')).toBeInTheDocument()
  })
  it('does not complete a Google login until the user explicitly continues', async () => {
    enabled()
    flow('Login')
    account()
    useAntiforgeryEndpoint()
    let completions = 0
    server.use(
      http.post(`${API}/auth/google/completions`, () => {
        completions++
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = await renderApp('/google/continue')
    const button = await screen.findByRole('button', { name: 'Entrar no Duora' })
    expect(completions).toBe(0)
    await user.click(button)
    expect(
      await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(completions).toBe(1)
  })
  it('completes with only a recovery code and never places it in mutation variables', async () => {
    enabled()
    flow('Mfa')
    account()
    useAntiforgeryEndpoint()
    const received: unknown[] = []
    server.use(
      http.post(`${API}/auth/google/completions`, async ({ request }) => {
        received.push(await request.json())
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user, queryClient } = await renderApp('/google/continue')
    const snapshots: string[] = []
    queryClient.getMutationCache().subscribe(() =>
      snapshots.push(
        JSON.stringify(
          queryClient
            .getMutationCache()
            .getAll()
            .map((m) => m.state),
        ),
      ),
    )
    await user.click(await screen.findByRole('button', { name: 'Usar um código de recuperação' }))
    await user.type(screen.getByLabelText('Código de recuperação'), 'synthetic-google-recovery')
    await user.click(screen.getByRole('button', { name: 'Entrar no Duora' }))
    expect(
      await screen.findByRole('heading', { name: 'Sua conta' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(received).toEqual([{ recoveryCode: 'synthetic-google-recovery' }])
    expect(snapshots.join('\n')).not.toContain('synthetic-google-recovery')
  })
  it('unknown identities get generic choices without an email-existence assertion', async () => {
    enabled()
    flow('Unknown')
    await renderApp('/google/continue')
    expect(await screen.findByRole('link', { name: 'Criar conta' })).toHaveAttribute('href', '/register')
    expect(screen.getByRole('link', { name: 'Entrar' })).toHaveAttribute('href', '/login')
    expect(screen.queryByText(/já existe|não existe/)).not.toBeInTheDocument()
  })
  it('requires local password and every legal declaration before external registration', async () => {
    enabled()
    flow('Register', 'synthetic@example.com')
    useAntiforgeryEndpoint()
    const received: unknown[] = []
    server.use(
      http.post(`${API}/auth/google/registrations`, async ({ request }) => {
        received.push(await request.json())
        flow('ConfirmEmail')
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const { user } = await renderApp('/google/continue')
    await user.type(await screen.findByLabelText('Senha'), 'synthetic local password')
    await user.click(screen.getByRole('button', { name: 'Criar conta' }))
    expect(received).toEqual([])
    for (const checkbox of screen.getAllByRole('checkbox')) await user.click(checkbox)
    await user.click(screen.getByRole('button', { name: 'Criar conta' }))
    await waitFor(() => {
      expect(received).toHaveLength(1)
    })
    expect(received[0]).toMatchObject({
      password: 'synthetic local password',
      confirmedAdult: true,
      acceptedTermsLocale: 'pt-BR',
      acceptedPrivacyLocale: 'pt-BR',
    })
    expect(await screen.findByLabelText('Código de confirmação')).toBeInTheDocument()
  })
  it('email-code confirmation requires the local password and leaves login to a new gesture', async () => {
    enabled()
    flow('ConfirmEmail')
    useAntiforgeryEndpoint()
    let completions = 0
    const received: unknown[] = []
    server.use(
      http.post(`${API}/auth/google/registration-confirmations`, async ({ request }) => {
        received.push(await request.json())
        return new HttpResponse(null, { status: 204 })
      }),
      http.post(`${API}/auth/google/completions`, () => {
        completions++
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = await renderApp('/google/continue')
    await user.type(await screen.findByLabelText('Código de confirmação'), '12345678')
    await user.type(screen.getByLabelText('Senha'), 'synthetic local password')
    await user.click(screen.getByRole('button', { name: 'Confirmar e-mail' }))
    expect(await screen.findByText('E-mail confirmado. Entre com Google para continuar.')).toBeInTheDocument()
    expect(received).toEqual([{ code: '12345678', password: 'synthetic local password' }])
    expect(completions).toBe(0)
  })
  it('never offers Google completion for an expired flow', async () => {
    enabled()
    server.use(
      http.get(`${API}/auth/google/flow`, () =>
        HttpResponse.json({ nextStep: 'Login', expiresAt: '2001-01-01T00:00:00Z' }),
      ),
    )
    await renderApp('/google/continue')
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Entrar no Duora' })).not.toBeInTheDocument()
  })
  it('link continuation requires an authenticated local session and explicit confirmation', async () => {
    enabled()
    flow('Link')
    account()
    let confirmations = 0
    server.use(
      http.get(`${API}/me/external-logins`, () => HttpResponse.json({ items: [] })),
      http.post(`${API}/me/external-logins/google/confirmations`, () => {
        confirmations++
        return new HttpResponse(null, { status: 204 })
      }),
    )
    await renderApp('/google/continue')
    expect(
      await screen.findByRole('button', { name: 'Confirmar vínculo com Google' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(confirmations).toBe(0)
  })
})

it('Google removal renews session, retains local login, and needs separate confirmation', async () => {
  enabled()
  const session = account()
  let linked = true,
    deletions = 0
  server.use(
    http.get(`${API}/me/external-logins`, () =>
      HttpResponse.json({ items: linked ? [{ provider: 'Google', linkedAt: '2026-10-01T00:00:00Z' }] : [] }),
    ),
    http.delete(`${API}/me/external-logins/google`, ({ request }) => {
      expect(request.headers.get('X-XSRF-TOKEN')).toBeTruthy()
      linked = false
      session.sid = TEST_RENEWED_SESSION_ID
      deletions++
      return new HttpResponse(null, { status: 204 })
    }),
  )
  const { user } = await renderApp('/account')
  await user.click(await screen.findByRole('button', { name: 'Remover Google' }))
  expect(deletions).toBe(0)
  await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))
  expect(
    await screen.findByText('Vínculo com Google removido. Sua senha do Duora continua funcionando.'),
  ).toBeInTheDocument()
  expect(deletions).toBe(1)
})

it('Google settings require local password step-up after reauthentication rejection without retrying the action', async () => {
  enabled()
  account()
  let removals = 0
  const reauth: unknown[] = []
  server.use(
    http.get(`${API}/me/external-logins`, () =>
      HttpResponse.json({ items: [{ provider: 'Google', linkedAt: '2026-10-01T00:00:00Z' }] }),
    ),
    http.delete(`${API}/me/external-logins/google`, () => {
      removals++
      return new HttpResponse(
        JSON.stringify({ status: 401, code: 'REAUTHENTICATION_REQUIRED', title: 'Unauthorized' }),
        { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
      )
    }),
    http.post(`${API}/auth/reauthenticate`, async ({ request }) => {
      reauth.push(await request.json())
      return new HttpResponse(null, { status: 204 })
    }),
  )
  const { user } = await renderApp('/account')
  await user.click(await screen.findByRole('button', { name: 'Remover Google' }))
  await user.click(screen.getByRole('button', { name: 'Confirmar remoção do Google' }))
  await user.type(await screen.findByLabelText('Senha do Duora'), 'synthetic local password')
  await user.click(screen.getByRole('button', { name: 'Confirmar identidade' }))
  expect(await screen.findByText('Identidade confirmada. Confirme a ação novamente.')).toBeInTheDocument()
  expect(reauth).toEqual([{ password: 'synthetic local password' }])
  expect(removals).toBe(1)
})
