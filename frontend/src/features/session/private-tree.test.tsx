import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import { useState } from 'react'
import { createQueryClient } from '@/app/query-client'
import { apiClient } from '@/shared/api'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import { API, problem, server } from '@/test/msw'
import { installTestMe, withSessionContext } from '@/test/private-context'
import { RequireSession } from './RequireSession'
import { usePrivateOperation } from './private-operation'
import { meQueryKey } from './query-keys'

const ACCOUNT = {
  id: '00000000-0000-4000-8000-000000000001',
  email: 'a@example.test',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
function Probe() {
  const operation = usePrivateOperation()
  const [draft, setDraft] = useState('')
  return (
    <section>
      <h1>private-tree</h1>
      <input
        aria-label="draft"
        value={draft}
        onChange={(event) => {
          setDraft(event.target.value)
        }}
      />
      <button
        onClick={() => {
          void operation
            .run((snapshot) =>
              apiClient.requestJson({ operation: snapshot, method: 'GET', path: '/api/v1/me/profile' }),
            )
            .catch(() => undefined)
        }}
      >
        private-read
      </button>
    </section>
  )
}
function mount(path = '/account', cache = createQueryClient()) {
  render(
    <I18nProvider initialLocale="pt-BR">
      <QueryClientProvider client={cache}>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route element={<RequireSession />}>
              <Route path="/account" element={<Probe />} />
              <Route path="/change-initial-password" element={<Probe />} />
            </Route>
            <Route path="/login" element={<p>login</p>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </I18nProvider>,
  )
  return cache
}

describe('árvore privada vinculada ao bootstrap', () => {
  it('Me sem contexto bloqueia a árvore privada', async () => {
    server.use(http.get(`${API}/me`, () => HttpResponse.json(ACCOUNT)))
    const cache = mount()
    await screen.findByRole('button', { name: 'Tentar de novo' })
    expect(screen.queryByText('private-tree')).not.toBeInTheDocument()
    expect(apiClient.sessionContext.getSnapshot().owner).toBeNull()
    cache.clear()
  })

  it('o 403 tipado de senha inicial libera apenas a árvore restrita pareada', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(
          {
            status: 403,
            code: 'PASSWORD_CHANGE_REQUIRED',
            sessionContext: { ...withSessionContext(ACCOUNT).sessionContext, phase: 'password-change' },
          },
          { status: 403 },
        ),
      ),
    )
    const cache = mount()
    await screen.findByText('private-tree')
    expect(apiClient.sessionContext.getSnapshot().owner).not.toBeNull()
    cache.clear()
  })

  it('refetch da mesma sessão mantém rascunho e owner', async () => {
    server.use(http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(ACCOUNT))))
    const cache = mount()
    await screen.findByText('private-tree')
    const owner = apiClient.sessionContext.getSnapshot().owner
    fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'not lost' } })
    await cache.invalidateQueries({ queryKey: meQueryKey })
    expect(await screen.findByLabelText('draft')).toHaveValue('not lost')
    expect(apiClient.sessionContext.getSnapshot().owner).toBe(owner)
    cache.clear()
  })

  it('403 em leitura privada remove a árvore e não busca Me novamente até o gesto explícito', async () => {
    let reads = 0
    server.use(
      http.get(`${API}/me`, () => {
        reads += 1
        return HttpResponse.json(withSessionContext(ACCOUNT))
      }),
      http.get(`${API}/me/profile`, () => problem(403, 'CSRF_VALIDATION_FAILED')),
    )
    const cache = mount()
    await screen.findByText('private-tree')
    fireEvent.click(screen.getByText('private-read'))
    const retry = await screen.findByRole('button', { name: 'Tentar de novo' })
    expect(screen.queryByText('private-tree')).not.toBeInTheDocument()
    window.dispatchEvent(new Event('focus'))
    await cache.invalidateQueries({ queryKey: meQueryKey })
    expect(reads).toBe(1)
    fireEvent.click(retry)
    await screen.findByText('private-tree')
    await waitFor(() => {
      expect(reads).toBe(2)
    })
    cache.clear()
  })
})

it('não monta o owner em cache ao voltar até o bootstrap desta montagem concluir', async () => {
  let release!: () => void
  let arrived!: () => void
  const held = new Promise<void>((resolve) => {
    release = resolve
  })
  const requested = new Promise<void>((resolve) => {
    arrived = resolve
  })
  server.use(
    http.get(`${API}/me`, async () => {
      arrived()
      await held
      return problem(503, 'TEMPORARILY_UNAVAILABLE')
    }),
  )
  const me = installTestMe({ ...ACCOUNT, status: 'Active' })
  const cache = createQueryClient()
  cache.setQueryData(meQueryKey, me)
  mount('/account', cache)
  await requested
  try {
    expect(screen.queryByText('private-tree')).not.toBeInTheDocument()
  } finally {
    release()
  }
  await screen.findByText('Não foi possível carregar sua conta.')
  expect(screen.queryByText('private-tree')).not.toBeInTheDocument()
  cache.clear()
})
