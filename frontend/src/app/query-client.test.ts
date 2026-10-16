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

describe('troca de identidade descoberta pela própria identidade (T-21)', () => {
  const anaId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
  const brunoId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'

  async function resolveMe(queryClient: ReturnType<typeof createQueryClient>, id: string): Promise<void> {
    await queryClient.query({ queryKey: meQueryKey, queryFn: () => Promise.resolve({ id }), staleTime: 0 })
  }

  it('identidade nova descarta os dados privados da anterior', async () => {
    const queryClient = createQueryClient()
    await resolveMe(queryClient, anaId)
    queryClient.setQueryData(sessionsQueryKey(anaId), [{ id: 'sessao-da-ana' }])

    await resolveMe(queryClient, brunoId)

    expect(queryClient.getQueryData(sessionsQueryKey(anaId))).toBeUndefined()
    expect(queryClient.getQueryData(meQueryKey)).toEqual({ id: brunoId })
  })

  it('identidade nova descarta também as escritas da anterior, com as variables', async () => {
    const queryClient = createQueryClient()
    await resolveMe(queryClient, anaId)
    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: (bio: string) => Promise.resolve(bio.length) })
      .execute('texto que a Ana escreveu')

    await resolveMe(queryClient, brunoId)

    expect(queryClient.getMutationCache().getAll()).toEqual([])
  })

  it('a mesma identidade reconsultada mantém o cache', async () => {
    const queryClient = createQueryClient()
    await resolveMe(queryClient, anaId)
    queryClient.setQueryData(sessionsQueryKey(anaId), [{ id: 'sessao-da-ana' }])

    await resolveMe(queryClient, anaId)

    expect(queryClient.getQueryData(sessionsQueryKey(anaId))).toEqual([{ id: 'sessao-da-ana' }])
  })

  it('a primeira identidade da aba não descarta nada', async () => {
    const queryClient = createQueryClient()
    queryClient.setQueryData(['publico'], 'dado anônimo')

    await resolveMe(queryClient, anaId)

    expect(queryClient.getQueryData(['publico'])).toBe('dado anônimo')
  })
})
