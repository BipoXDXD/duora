import { ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'

const UNAUTHORIZED = 401
const TOO_MANY_REQUESTS = 429

/**
 * O que a interface precisa saber de uma falha para escolher a mensagem: rede é distinto de
 * resposta (spec 13.2), limite de uso traz a espera e o resto decide pelo `code` estável.
 */
export type RequestFailure =
  | { readonly kind: 'network' }
  | { readonly kind: 'rate-limited'; readonly retryAfterSeconds: number | null }
  | { readonly kind: 'problem'; readonly status: number; readonly code: string | null }
  | { readonly kind: 'unexpected' }

export function classifyRequestFailure(error: unknown): RequestFailure {
  if (error instanceof ApiNetworkError) {
    return { kind: 'network' }
  }
  if ((error instanceof ApiProblemError || error instanceof ApiResponseError) && error.status === TOO_MANY_REQUESTS) {
    return { kind: 'rate-limited', retryAfterSeconds: error.retryAfterSeconds }
  }
  if (error instanceof ApiProblemError) {
    return { kind: 'problem', status: error.status, code: error.problem.code ?? null }
  }
  return { kind: 'unexpected' }
}

/** A sessão não existe ou expirou (401), em qualquer formato de resposta. */
export function isUnauthorized(error: unknown): boolean {
  return (error instanceof ApiProblemError || error instanceof ApiResponseError) && error.status === UNAUTHORIZED
}

/** Somente códigos de autorização autenticados, nunca texto livre do servidor. */
export function isSessionRestriction(error: unknown): boolean {
  return (
    error instanceof ApiProblemError &&
    error.status === 403 &&
    (error.problem.code === 'PASSWORD_CHANGE_REQUIRED' || error.problem.code === 'MFA_ENROLLMENT_REQUIRED')
  )
}
