import { installTestMe, withSessionContext } from '@/test/private-context'
import { PrivateOperationProvider } from './PrivateOperationProvider'
import { act, renderHook, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LoginPage } from '@/features/auth/LoginPage'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, TestProviders } from '@/test/render'
import { identityBroadcast } from './identity-broadcast'
import type { AccountSession, Me } from './session-api'
import { meQueryKey, sessionsQueryKey } from './query-keys'
import { useLogout, useMe, useRevokeSession, useSessions } from './session-hooks'

let ME: Me
const ACCOUNT: Omit<Me, 'owner'> = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
  currentTermsAccepted: true,
}

beforeEach(() => {
  ME = installTestMe(ACCOUNT)
})

function session(overrides: Partial<AccountSession>): AccountSession {
  return {
    id: 'sessao-1',
    createdAt: '2026-10-14T10:00:00Z',
    lastSeenAt: '2026-10-15T09:30:00Z',
    current: false,
    description: 'Firefox no Ubuntu',
    ...overrides,
  }
}

function seededQueryClient() {
  const queryClient = createTestQueryClient()
  queryClient.setQueryData(meQueryKey, ME)
  queryClient.setQueryData(sessionsQueryKey(ME.id), [session({ current: true })])
  return queryClient
}

/** O hook roda na área da conta; a rota de entrada é a real, que descarta o cache ao montar. */
function renderSessionHook<T>(hook: () => T, queryClient = seededQueryClient()) {
  const view = renderHook(hook, {
    wrapper: ({ children }) => (
      <TestProviders queryClient={queryClient} initialPath="/account">
        <Routes>
          <Route
            path="/account"
            element={<PrivateOperationProvider owner={ME.owner}>{children}</PrivateOperationProvider>}
          />
          <Route path="/login" element={<LoginPage />} />
        </Routes>
      </TestProviders>
    ),
  })
  return { ...view, queryClient }
}

describe('useLogout', () => {
  it('limpa todo o cache e leva à entrada sem renovar CSRF público', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })))
    const { result, queryClient } = renderSessionHook(useLogout)

    await act(() => result.current.mutateAsync())

    expect(await screen.findByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(queryClient.getQueryCache().getAll()).toEqual([])
    // A intenção privada nunca pede um token público, inclusive após a saída.
    await waitFor(() => {
      expect(antiforgery.calls()).toBe(0)
    })
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/login$/)
  })

  it('avisa as outras abas de que a identidade mudou (T-21)', async () => {
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })))
    const announce = vi.spyOn(identityBroadcast, 'announce')
    const { result } = renderSessionHook(useLogout)

    await act(() => result.current.mutateAsync())

    expect(announce).toHaveBeenCalledOnce()
  })

  it('mantém o cache quando o servidor não confirma a saída', async () => {
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => HttpResponse.error()))
    const { result, queryClient } = renderSessionHook(useLogout)

    await act(() => result.current.mutateAsync().catch(() => undefined))

    await waitFor(() => {
      expect(result.current.isError).toBe(true)
    })
    expect(queryClient.getQueryData(meQueryKey)).toEqual(ME)
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/account$/)
  })
})

describe('useRevokeSession', () => {
  it('revogar outra sessão só recarrega a lista', async () => {
    useAntiforgeryEndpoint()
    server.use(
      http.delete(`${API}/me/sessions/:id`, () => new HttpResponse(null, { status: 204 })),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [session({ current: true })] })),
    )
    const { result, queryClient } = renderSessionHook(() => useRevokeSession(ME.id))

    await act(() => result.current.mutateAsync(session({ id: 'outra' })))

    expect(queryClient.getQueryData(meQueryKey)).toEqual(ME)
    expect(queryClient.getQueryState(sessionsQueryKey(ME.id))?.isInvalidated).toBe(true)
  })

  it('revogar a sessão corrente equivale a sair: limpa o cache sem pedir CSRF público', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    server.use(http.delete(`${API}/me/sessions/:id`, () => new HttpResponse(null, { status: 204 })))
    const { result, queryClient } = renderSessionHook(() => useRevokeSession(ME.id))

    await act(() => result.current.mutateAsync(session({ current: true })))

    expect(await screen.findByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(queryClient.getQueryCache().getAll()).toEqual([])
    await waitFor(() => {
      expect(antiforgery.calls()).toBe(0)
    })
  })

  it('401 na revogação reconsulta a identidade', async () => {
    useAntiforgeryEndpoint()
    server.use(
      http.delete(`${API}/me/sessions/:id`, () => problem(401, 'UNAUTHENTICATED')),
      http.get(`${API}/me`, () => problem(401, 'UNAUTHENTICATED')),
    )
    const { result, queryClient } = renderSessionHook(() => useRevokeSession(ME.id))

    await act(() => result.current.mutateAsync(session({ id: 'outra' })).catch(() => undefined))

    expect(queryClient.getQueryState(meQueryKey)?.isInvalidated).toBe(true)
  })
})

describe('dados privados fora da área autenticada (T-14, T-21)', () => {
  it.each([
    ['identidade', (): unknown => useMe(), meQueryKey],
    ['lista de sessões', (): unknown => useSessions(ME.id), sessionsQueryKey(ACCOUNT.id)],
  ])('a %s sai da memória quando a área desmonta; voltar a ela pergunta ao servidor', async (_caso, hook, queryKey) => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(ME))),
      http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [session({ current: true })] })),
    )
    const queryClient = createTestQueryClient()
    const view = renderHook(hook, {
      wrapper: ({ children }) => (
        <TestProviders queryClient={queryClient} initialPath="/account">
          <PrivateOperationProvider owner={ME.owner}>{children}</PrivateOperationProvider>
        </TestProviders>
      ),
    })
    await waitFor(() => {
      expect(queryClient.getQueryData(queryKey)).toBeDefined()
    })

    view.unmount()

    await waitFor(() => {
      expect(queryClient.getQueryCache().find({ queryKey })).toBeUndefined()
    })
  })
})
