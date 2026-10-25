// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  bindPrivateRequest,
  bindPublicRequest,
  createSessionContextStore,
  type SessionContextStore,
  type ValidatedBootstrap,
} from './session-context'

const USER_A = '00000000-0000-4000-8000-000000000001'
const USER_B = '00000000-0000-4000-8000-000000000002'
const SESSION_A = '00000000-0000-4000-8000-000000000011'
const SESSION_B = '00000000-0000-4000-8000-000000000012'
const AUTH_VIEW = { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] }
const A: ValidatedBootstrap = {
  userId: USER_A,
  sessionId: SESSION_A,
  phase: 'active',
  requestToken: 'private-proof-A',
  authView: AUTH_VIEW,
}

function install(store: SessionContextStore, binding = A) {
  const owner = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), binding)
  store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  return owner
}

function privateRead() {
  return new Request('https://duora.test/api/v1/me/time-zone', { headers: { 'X-XSRF-TOKEN': 'forged' } })
}

describe('contexto privado em memória', () => {
  it('não libera operações antes da barreira de desmontagem/cache', () => {
    const store = createSessionContextStore()
    const owner = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), A)

    expect(store.getSnapshot().owner).toBeNull()
    expect(store.getSnapshot().pendingOwner).toBe(owner)
    expect(() => store.begin(owner)).toThrow(expect.objectContaining({ name: 'AbortError' }))
    expect(() => {
      store.completeOwnerInstallation(-1, owner)
    }).toThrow()
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
    const request = bindPrivateRequest(store, store.begin(owner), privateRead())
    expect(request.headers.get('X-XSRF-TOKEN')).toBe('private-proof-A')
  })

  it('refetch da mesma sessão conserva dono e a prova imutável da operação já iniciada', () => {
    const store = createSessionContextStore()
    const owner = install(store)
    const earlier = store.begin(owner)
    const refreshed = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), {
      ...A,
      requestToken: 'private-proof-A-new',
    })

    expect(refreshed).toBe(owner)
    expect(store.getSnapshot().owner).toBe(owner)
    expect(bindPrivateRequest(store, earlier, privateRead()).headers.get('X-XSRF-TOKEN')).toBe('private-proof-A')
    expect(bindPrivateRequest(store, store.begin(owner), privateRead()).headers.get('X-XSRF-TOKEN')).toBe(
      'private-proof-A-new',
    )
  })

  it.each([
    { name: 'outra pessoa', binding: { ...A, userId: USER_B, sessionId: SESSION_B } },
    { name: 'mesma pessoa em nova sessão', binding: { ...A, sessionId: SESSION_B } },
    { name: 'outra fase', binding: { ...A, phase: 'mfa-enrollment' as const } },
    { name: 'novo papel', binding: { ...A, authView: { ...AUTH_VIEW, roles: ['moderator'] } } },
    { name: 'status restrito', binding: { ...A, authView: { ...AUTH_VIEW, status: 'Restricted' } } },
    { name: 'MFA alterado', binding: { ...A, authView: { ...AUTH_VIEW, mfaEnabled: true } } },
  ])('invalida antes de enviar sob $name', ({ binding }) => {
    const store = createSessionContextStore()
    const owner = install(store)
    const operation = store.begin(owner)
    const request = bindPrivateRequest(store, operation, privateRead())

    const replacement = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), binding)

    expect(replacement).not.toBe(owner)
    expect(request.signal.aborted).toBe(true)
    expect(() => bindPrivateRequest(store, operation, privateRead())).toThrow(
      expect.objectContaining({ name: 'AbortError' }),
    )
    expect(store.getSnapshot().owner).toBeNull()
    expect(store.getSnapshot().pendingOwner).toBe(replacement)
  })

  it('papéis reordenados e token aleatório novo não desmontam o mesmo contexto', () => {
    const store = createSessionContextStore()
    const owner = install(store, { ...A, authView: { ...AUTH_VIEW, roles: ['moderator', 'product_admin'] } })
    const same = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), {
      ...A,
      requestToken: 'another-random-proof',
      authView: { ...AUTH_VIEW, roles: ['product_admin', 'moderator'] },
    })
    expect(same).toBe(owner)
  })

  it('resposta automática antiga não desbloqueia uma proteção persistente', () => {
    const store = createSessionContextStore()
    install(store)
    const automatic = store.beginBootstrap({ kind: 'automatic' })
    store.protect('context')

    expect(() => store.acceptBootstrap(automatic, A)).toThrow(expect.objectContaining({ name: 'AbortError' }))
    expect(() => store.beginBootstrap({ kind: 'automatic' })).toThrow()
    expect(store.getSnapshot().protected).toBe(true)

    const click = store.beginExplicitRevalidation()
    const owner = store.acceptBootstrap(store.beginBootstrap({ kind: 'explicit', ticket: click }), A)
    expect(store.getSnapshot().protected).toBe(true)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
    expect(store.getSnapshot().protected).toBe(false)
  })

  it('só o clique mais recente pode estabelecer o contexto protegido', () => {
    const store = createSessionContextStore()
    store.protect('identity')
    const first = store.beginBootstrap({ kind: 'explicit', ticket: store.beginExplicitRevalidation() })
    const second = store.beginBootstrap({ kind: 'explicit', ticket: store.beginExplicitRevalidation() })

    expect(() => store.acceptBootstrap(first, A)).toThrow()
    const current = store.acceptBootstrap(second, A)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, current)
    expect(store.getSnapshot().owner).toBe(current)
  })

  it('um ack atrasado nunca ativa o dono que já foi substituído', () => {
    const store = createSessionContextStore()
    const first = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), A)
    const oldEpoch = store.getSnapshot().transitionEpoch
    const second = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), { ...A, sessionId: SESSION_B })
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, second)

    expect(() => {
      store.completeOwnerInstallation(oldEpoch, first)
    }).toThrow()
    expect(store.getSnapshot().owner).toBe(second)
  })

  it('descarta resposta de bootstrap fora de ordem sem restaurar prova antiga', () => {
    const store = createSessionContextStore()
    const owner = install(store)
    const older = store.beginBootstrap({ kind: 'automatic' })
    const newer = store.beginBootstrap({ kind: 'automatic' })
    store.acceptBootstrap(newer, { ...A, requestToken: 'newest-proof' })

    expect(() => store.acceptBootstrap(older, A)).toThrow()
    expect(bindPrivateRequest(store, store.begin(owner), privateRead()).headers.get('X-XSRF-TOKEN')).toBe(
      'newest-proof',
    )
  })

  it('propaga cancelamento do chamador e rejeita depois de finalizar', () => {
    const store = createSessionContextStore()
    const operation = store.begin(install(store))
    const caller = new AbortController()
    const request = bindPrivateRequest(
      store,
      operation,
      new Request('https://duora.test/api/v1/me/time-zone', { signal: caller.signal }),
    )
    caller.abort()
    expect(request.signal.aborted).toBe(true)
    expect(() => {
      store.assertCurrent(operation)
    }).toThrow()
    store.finish(operation)
    store.finish(operation)
    expect(() => {
      store.assertCurrent(operation)
    }).toThrow()
  })

  it('não deixa referências opacas serializarem prova ou identificadores', () => {
    const store = createSessionContextStore()
    const owner = install(store)
    const serialized = JSON.stringify({ owner, operation: store.begin(owner), state: store.getSnapshot() })
    expect(serialized).not.toContain('private-proof-A')
    expect(serialized).not.toContain(USER_A)
    expect(serialized).not.toContain(SESSION_A)
  })

  it('não aceita operação de outro store nem finalidade pública em chamada privada', () => {
    const store = createSessionContextStore()
    const other = createSessionContextStore()
    const operation = store.begin(install(store))
    expect(() => bindPrivateRequest(other, operation, privateRead())).toThrow()
    expect(() => bindPrivateRequest(store, store.beginPublic(), privateRead())).toThrow()
    expect(() => bindPublicRequest(store, operation, new Request('https://duora.test/api/v1/auth/csrf'))).toThrow()
  })

  it('a geração pública também não atravessa CSRF→POST depois de trocar identidade', () => {
    const store = createSessionContextStore()
    const publicIntent = store.beginPublic()
    store.protect('identity')
    expect(() => {
      store.assertCurrent(publicIntent)
    }).toThrow(expect.objectContaining({ name: 'AbortError' }))
    const loginIntent = store.beginPublic()
    expect(() => {
      store.assertCurrent(loginIntent)
    }).not.toThrow()
    store.finish(loginIntent)
  })

  it('a primeira identidade privada também encerra uma intenção pública anterior', () => {
    const store = createSessionContextStore()
    const earlier = store.beginPublic()

    install(store)

    expect(() => {
      store.assertCurrent(earlier)
    }).toThrow(expect.objectContaining({ name: 'AbortError' }))
  })

  it('um clique autoriza apenas uma leitura de bootstrap, sem replay automático do ticket', () => {
    const store = createSessionContextStore()
    store.protect('context')
    const click = store.beginExplicitRevalidation()
    const first = store.beginBootstrap({ kind: 'explicit', ticket: click })

    expect(() => store.beginBootstrap({ kind: 'explicit', ticket: click })).toThrow(
      expect.objectContaining({ name: 'AbortError' }),
    )
    const owner = store.acceptBootstrap(first, A)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  })
  it('revalidação explícita em sessão ativa protege antes de qualquer await', () => {
    const store = createSessionContextStore()
    const owner = install(store)
    const operation = store.begin(owner)
    store.beginExplicitRevalidation()
    expect(store.getSnapshot()).toMatchObject({ protected: true, owner: null, pendingOwner: null })
    expect(() => {
      store.assertCurrent(operation)
    }).toThrow(expect.objectContaining({ name: 'AbortError' }))
  })
})
