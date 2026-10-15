// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from '@/shared/api/api-errors'
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
})
