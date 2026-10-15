// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'
import { classifyRequestFailure, isUnauthorized } from './request-failure'

const problem = (status: number, code?: string) =>
  new ApiProblemError({ type: 'about:blank', title: 'Erro', status, ...(code === undefined ? {} : { code }) })

describe('classifyRequestFailure', () => {
  it('classifica falha de rede', () => {
    expect(classifyRequestFailure(new ApiNetworkError(new TypeError('offline')))).toEqual({ kind: 'network' })
  })

  it('classifica 429 com Problem Details levando o Retry-After', () => {
    expect(classifyRequestFailure(new ApiProblemError({ type: 'x', title: 'Limite', status: 429 }, 42))).toEqual({
      kind: 'rate-limited',
      retryAfterSeconds: 42,
    })
  })

  it('classifica 429 fora do contrato levando o Retry-After', () => {
    expect(classifyRequestFailure(new ApiResponseError(429, 'sem corpo', null))).toEqual({
      kind: 'rate-limited',
      retryAfterSeconds: null,
    })
  })

  it('classifica Problem Details com code', () => {
    expect(classifyRequestFailure(problem(401, 'INVALID_CREDENTIALS'))).toEqual({
      kind: 'problem',
      status: 401,
      code: 'INVALID_CREDENTIALS',
    })
  })

  it('classifica Problem Details sem code com code null', () => {
    expect(classifyRequestFailure(problem(400))).toEqual({ kind: 'problem', status: 400, code: null })
  })

  it.each([
    ['resposta fora do contrato', new ApiResponseError(502, 'gateway')],
    ['erro de programação', new TypeError('x is undefined')],
    ['valor que não é erro', 'falhou'],
  ])('classifica %s como inesperado', (_caso, error) => {
    expect(classifyRequestFailure(error)).toEqual({ kind: 'unexpected' })
  })
})

describe('isUnauthorized', () => {
  it.each([
    ['401 com Problem Details', problem(401), true],
    ['401 fora do contrato', new ApiResponseError(401, 'sem corpo'), true],
    ['403', problem(403), false],
    ['rede', new ApiNetworkError(new TypeError('offline')), false],
    ['erro comum', new Error('x'), false],
  ])('%s → %s', (_caso, error, expected) => {
    expect(isUnauthorized(error)).toBe(expected)
  })
})
