import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { createApiClient, type ApiFetch } from '@/shared/api/api-client'
import { createSessionContextStore, type SessionContextStore } from '@/shared/session-context'
import { createQueryClient } from '@/app/query-client'
import { fetchMe } from './session-api'
import { meQueryKey } from './query-keys'

const A = '00000000-0000-4000-8000-000000000001'
const SID = '00000000-0000-4000-8000-000000000011'
const ACCOUNT = {
  id: A,
  email: 'a@example.test',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
const CONTEXT = { userId: A, sessionId: SID, phase: 'active', requestToken: 'private-secret-A' }
function clientFor(store: SessionContextStore, fetch: ApiFetch) {
  return createApiClient({
    sessionContext: store,
    fetch,
    origin: location.origin,
    readAntiforgeryToken: () => null,
    readLocale: () => 'pt-BR',
    now: () => 0,
  })
}
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status === 403 ? 'application/problem+json' : 'application/json' },
  })
}

describe('bootstrap atômico e cache real de Me', () => {
  it('retorna corpo+owner pareados, sem prova/SID/corpo bruto no cache', async () => {
    const store = createSessionContextStore()
    const client = clientFor(store, () => Promise.resolve(json({ ...ACCOUNT, sessionContext: CONTEXT })))
    const me = await fetchMe(undefined, { kind: 'automatic' }, client)
    expect(me).toMatchObject(ACCOUNT)
    expect(me.owner).toBe(store.getSnapshot().pendingOwner)
    expect(store.getSnapshot().owner).toBeNull()
    const cache = new QueryClient()
    cache.setQueryData(meQueryKey, me)
    const serialized = JSON.stringify(cache.getQueryData(meQueryKey))
    expect(serialized).not.toContain('private-secret-A')
    expect(serialized).not.toContain(SID)
    expect(serialized).not.toContain('sessionContext')
    cache.clear()
  })

  it.each([
    { ...ACCOUNT },
    { ...ACCOUNT, sessionContext: { ...CONTEXT, userId: '00000000-0000-4000-8000-000000000002' } },
    { ...ACCOUNT, sessionContext: { ...CONTEXT, phase: 'password-change' } },
    { ...ACCOUNT, sessionContext: { ...CONTEXT, phase: 'mfa-enrollment' } },
    { ...ACCOUNT, sessionContext: { ...CONTEXT, requestToken: '' } },
  ])('rejeita bootstrap incompleto/incoerente sem instalar capacidade', async (body) => {
    const store = createSessionContextStore()
    const client = clientFor(store, () => Promise.resolve(json(body)))
    await expect(fetchMe(undefined, { kind: 'automatic' }, client)).rejects.toThrow()
    expect(store.getSnapshot()).toMatchObject({ owner: null, pendingOwner: null })
  })

  it('403 de senha inicial transporta só a restrição e o owner opaco', async () => {
    const store = createSessionContextStore()
    const client = clientFor(store, () =>
      Promise.resolve(
        json(
          { status: 403, code: 'PASSWORD_CHANGE_REQUIRED', sessionContext: { ...CONTEXT, phase: 'password-change' } },
          403,
        ),
      ),
    )
    const error = await fetchMe(undefined, { kind: 'automatic' }, client).catch((value: unknown) => value as Error)
    expect(error).toMatchObject({
      status: 403,
      code: 'PASSWORD_CHANGE_REQUIRED',
      owner: store.getSnapshot().pendingOwner,
    })
    expect(store.getSnapshot().pendingOwner).not.toBeNull()
    expect(JSON.stringify(error)).not.toContain('private-secret-A')
    expect(JSON.stringify(error)).not.toContain(SID)
  })

  it('mesma sessão mantém owner; novo SID sobrevive ao cache mesmo com os mesmos campos públicos', async () => {
    const store = createSessionContextStore()
    let context = CONTEXT
    const client = clientFor(store, () => Promise.resolve(json({ ...ACCOUNT, sessionContext: context })))
    const first = await fetchMe(undefined, { kind: 'automatic' }, client)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, first.owner)
    context = { ...context, requestToken: 'new-random-token' }
    const same = await fetchMe(undefined, { kind: 'automatic' }, client)
    expect(same.owner).toBe(first.owner)
    const cache = createQueryClient(store)
    cache.setQueryData(meQueryKey, first)
    context = { ...context, sessionId: '00000000-0000-4000-8000-000000000012' }
    const second = await fetchMe(undefined, { kind: 'automatic' }, client)
    cache.setQueryData(meQueryKey, second)
    expect(second.owner).not.toBe(first.owner)
    expect(cache.getQueryData(meQueryKey)).toBe(second)
    expect(cache.getQueryDefaults(meQueryKey).structuralSharing).toBe(false)
    cache.clear()
  })
})

it.each(['server-error', 'invalid-json', 'network-error'] as const)(
  'erro de bootstrap antigo%s não protege o donoC já confirmado',
  async (failure) => {
    const store = createSessionContextStore()
    let old = true
    let entered!: () => void
    let release!: () => void
    const parsedFailure = new Promise<void>((resolve) => {
      entered = resolve
    })
    const resumeCatch = new Promise<void>((resolve) => {
      release = resolve
    })
    const client = clientFor(store, () => {
      if (!old)
        return Promise.resolve(
          json({ ...ACCOUNT, sessionContext: { ...CONTEXT, sessionId: '00000000-0000-4000-8000-000000000013' } }),
        )
      if (failure === 'network-error') return Promise.reject(new TypeError('synthetic offline'))
      if (failure === 'invalid-json')
        return Promise.resolve(new Response('invalid-json', { headers: { 'Content-Type': 'application/json' } }))
      return Promise.resolve(json({ status: 503, title: 'unavailable' }, 503))
    })
    // Mantém o transporte/parsing reais; segura só a entrega do erro já produzido ao consumidor async.
    const delayedClient = {
      ...client,
      readMeBootstrap: async (...args: Parameters<typeof client.readMeBootstrap>) => {
        try {
          return await client.readMeBootstrap(...args)
        } catch (error) {
          entered()
          await resumeCatch
          throw error
        }
      },
    }
    const earlier = fetchMe(undefined, { kind: 'automatic' }, delayedClient).catch((error: unknown) => error as Error)
    await parsedFailure
    old = false
    const c = await fetchMe(undefined, { kind: 'automatic' }, client)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, c.owner)
    const accepted = store.getSnapshot()
    release()
    const rejected = await earlier
    expect(store.getSnapshot()).toBe(accepted)
    expect(rejected).toMatchObject({ name: 'AbortError' })
    expect(store.getSnapshot().owner).toBe(c.owner)
  },
)
