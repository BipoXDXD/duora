import { act, renderHook, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import { LoginPage } from '@/features/auth/LoginPage'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, TestProviders } from '@/test/render'
import type { AccountSession, Me } from './session-api'
import { meQueryKey, sessionsQueryKey } from './query-keys'
import { useLogout, useRevokeSession } from './session-hooks'

const ME: Me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
}

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
      <TestProviders queryClient={queryClient} initialPath="/conta">
        <Routes>
          <Route path="/conta" element={children} />
          <Route path="/entrar" element={<LoginPage />} />
        </Routes>
      </TestProviders>
    ),
  })
  return { ...view, queryClient }
}

describe('useLogout', () => {
  it('limpa todo o cache, renova o CSRF e leva à entrada', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })))
    const { result, queryClient } = renderSessionHook(useLogout)

    await act(() => result.current.mutateAsync())

    expect(await screen.findByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(queryClient.getQueryCache().getAll()).toEqual([])
    // Um token antes do POST e outro depois, já para a sessão anônima.
    await waitFor(() => {
      expect(antiforgery.calls()).toBe(2)
    })
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/entrar$/)
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
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta$/)
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

  it('revogar a sessão corrente equivale a sair: limpa o cache e renova o CSRF', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    server.use(http.delete(`${API}/me/sessions/:id`, () => new HttpResponse(null, { status: 204 })))
    const { result, queryClient } = renderSessionHook(() => useRevokeSession(ME.id))

    await act(() => result.current.mutateAsync(session({ current: true })))

    expect(await screen.findByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(queryClient.getQueryCache().getAll()).toEqual([])
    await waitFor(() => {
      expect(antiforgery.calls()).toBe(2)
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
