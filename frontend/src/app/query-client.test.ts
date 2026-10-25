import { createSessionContextStore } from '@/shared/session-context'
import { acknowledgeOwnerInstallation } from '@/features/session/owner-installation'
// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from '@/shared/api/api-errors'
import { meQueryKey, sessionsQueryKey } from '@/features/session/query-keys'
import { createQueryClient, shouldRetryQuery } from './query-client'

const problem = (status: number) => new ApiProblemError({ type: 'about:blank', title: 'Erro', status })

describe('shouldRetryQuery', () => {
  it.each([
    ['falha de rede', new ApiNetworkError(new TypeError('offline'))],
    ['503 com Problem Details', problem(503)],
    ['502 sem Problem Details', new ApiResponseError(502, 'gateway')],
  ])('repete leitura após %s enquanto houver tentativas', (_caso, error) => {
    expect(shouldRetryQuery(0, error)).toBe(true)
    expect(shouldRetryQuery(1, error)).toBe(true)
  })

  it('para depois de duas novas tentativas', () => {
    expect(shouldRetryQuery(2, new ApiNetworkError(new TypeError('offline')))).toBe(false)
  })

  it.each([
    ['400', problem(400)],
    ['401', problem(401)],
    ['404', problem(404)],
    ['409', problem(409)],
    ['429', problem(429)],
    ['200 fora do contrato', new ApiResponseError(200, 'sem JSON')],
    ['cancelamento', new DOMException('cancelado', 'AbortError')],
    ['erro de programação', new TypeError('x is undefined')],
  ])('não repete após %s', (_caso, error) => {
    expect(shouldRetryQuery(0, error)).toBe(false)
  })
})

describe('createQueryClient', () => {
  it('nunca repete mutações automaticamente', () => {
    expect(createQueryClient().getDefaultOptions().mutations?.retry).toBe(false)
  })

  it('401 numa leitura privada invalida a identidade, para a área protegida levar à entrada', async () => {
    const queryClient = createQueryClient()
    queryClient.setQueryData(meQueryKey, { id: 'u1' })

    await queryClient
      .query({ queryKey: sessionsQueryKey('u1'), queryFn: () => Promise.reject(problem(401)), retry: false })
      .catch(() => undefined)

    expect(queryClient.getQueryState(meQueryKey)?.isInvalidated).toBe(true)
  })

  it('401 na própria identidade não se reinvalida em laço', async () => {
    const queryClient = createQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await queryClient
      .query({ queryKey: meQueryKey, queryFn: () => Promise.reject(problem(401)), retry: false })
      .catch(() => undefined)

    expect(invalidate).not.toHaveBeenCalled()
  })

  it.each([
    ['403', problem(403)],
    ['rede', new ApiNetworkError(new TypeError('offline'))],
  ])('%s numa leitura privada não mexe na identidade', async (_caso, error) => {
    const queryClient = createQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await queryClient
      .query({ queryKey: sessionsQueryKey('u1'), queryFn: () => Promise.reject(error), retry: false })
      .catch(() => undefined)

    expect(invalidate).not.toHaveBeenCalled()
  })

  it('401 numa escrita invalida a identidade, para a área protegida levar à entrada (T-14)', async () => {
    const queryClient = createQueryClient()
    queryClient.setQueryData(meQueryKey, { id: 'u1' })

    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: () => Promise.reject(problem(401)) })
      .execute(undefined)
      .catch(() => undefined)

    expect(queryClient.getQueryState(meQueryKey)?.isInvalidated).toBe(true)
  })

  it.each([
    ['403', problem(403)],
    ['409', problem(409)],
    ['rede', new ApiNetworkError(new TypeError('offline'))],
  ])('%s numa escrita não mexe na identidade', async (_caso, error) => {
    const queryClient = createQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: () => Promise.reject(error) })
      .execute(undefined)
      .catch(() => undefined)

    expect(invalidate).not.toHaveBeenCalled()
  })
})

describe('troca de identidade após o commit neutro (T-21)', () => {
  const anaId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
  const brunoId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
  function fixture() {
    const store = createSessionContextStore()
    return { store, client: createQueryClient(store) }
  }
  function resolveMe({ client, store }: ReturnType<typeof fixture>, id: string) {
    const ticket = store.beginBootstrap({ kind: 'automatic' })
    const owner = store.acceptBootstrap(ticket, {
      userId: id,
      sessionId: '0199f1a2-3b4c-7d5e-8f60-000000000001',
      phase: 'active',
      requestToken: 'synthetic',
      authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] },
    })
    store.finishBootstrap(ticket)
    client.setQueryData(meQueryKey, { id, owner })
    return Promise.resolve(owner)
  }
  it('novo dono só remove consultas e escritas após confirmação da desmontagem', async () => {
    const current = fixture()
    const first = await resolveMe(current, anaId)
    await acknowledgeOwnerInstallation(current.store, current.client, {
      owner: first,
      epoch: current.store.getSnapshot().transitionEpoch,
    })
    current.client.setQueryData(sessionsQueryKey(anaId), [{ id: 'sessao-da-ana' }])
    await current.client
      .getMutationCache()
      .build(current.client, { mutationFn: (bio: string) => Promise.resolve(bio.length) })
      .execute('rascunho privado')
    const next = await resolveMe(current, brunoId)
    expect(current.store.getSnapshot().owner).toBeNull()
    expect(current.client.getQueryData(sessionsQueryKey(anaId))).toBeDefined()
    expect(current.client.getMutationCache().getAll()).toHaveLength(1)
    await acknowledgeOwnerInstallation(current.store, current.client, {
      owner: next,
      epoch: current.store.getSnapshot().transitionEpoch,
    })
    expect(current.client.getQueryData(sessionsQueryKey(anaId))).toBeUndefined()
    expect(current.client.getMutationCache().getAll()).toEqual([])
    expect(current.client.getQueryData(meQueryKey)).toEqual({ id: brunoId, owner: next })
    current.client.clear()
  })
  it('mesmo contexto reconsultado preserva o cache sem nova barreira', async () => {
    const current = fixture()
    const owner = await resolveMe(current, anaId)
    await acknowledgeOwnerInstallation(current.store, current.client, {
      owner,
      epoch: current.store.getSnapshot().transitionEpoch,
    })
    current.client.setQueryData(sessionsQueryKey(anaId), [{ id: 'sessao-da-ana' }])
    expect(await resolveMe(current, anaId)).toBe(owner)
    expect(current.store.getSnapshot().pendingOwner).toBeNull()
    expect(current.client.getQueryData(sessionsQueryKey(anaId))).toEqual([{ id: 'sessao-da-ana' }])
    current.client.clear()
  })
})

describe('restrições da sessão', () => {
  it.each(['PASSWORD_CHANGE_REQUIRED', 'MFA_ENROLLMENT_REQUIRED'])(
    'reconsulta a identidade em leitura e escrita: %s',
    async (code) => {
      const client = createQueryClient()
      const error = new ApiProblemError({ type: 'about:blank', title: code, status: 403, code })
      client.setQueryData(meQueryKey, { id: 'u1' })
      await client.query({ queryKey: ['private'], queryFn: () => Promise.reject(error) }).catch(() => undefined)
      expect(client.getQueryState(meQueryKey)?.isInvalidated).toBe(true)
      client.setQueryData(meQueryKey, { id: 'u1' })
      await client
        .getMutationCache()
        .build(client, { mutationFn: () => Promise.reject(error) })
        .execute(undefined)
        .catch(() => undefined)
      expect(client.getQueryState(meQueryKey)?.isInvalidated).toBe(true)
    },
  )
})

it.each(['INVALID_CREDENTIALS', 'REAUTHENTICATION_REQUIRED'])(
  'recusa autenticada%s conserva o formulário, sem uma reconsulta de identidade redundante',
  async (code) => {
    const context = createSessionContextStore()
    const client = createQueryClient(context)
    client.setQueryData(meQueryKey, { id: 'synthetic' })
    const error = new ApiProblemError({ type: 'about:blank', title: code, status: 401, code })
    await client
      .getMutationCache()
      .build(client, { mutationFn: () => Promise.reject(error) })
      .execute(undefined)
      .catch(() => undefined)
    expect(client.getQueryState(meQueryKey)?.isInvalidated).toBe(false)
    client.clear()
  },
)
