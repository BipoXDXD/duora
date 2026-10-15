import { QueryClient } from '@tanstack/react-query'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from '@/shared/api/api-errors'

const MAX_QUERY_RETRIES = 2
const FIRST_SERVER_ERROR_STATUS = 500

/**
 * Leituras repetem só falhas transitórias (rede e 5xx). Erros 4xx são respostas definitivas
 * do servidor e repetir só atrasaria a mensagem ao usuário.
 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  return failureCount < MAX_QUERY_RETRIES && isTransient(error)
}

function isTransient(error: unknown): boolean {
  if (error instanceof ApiNetworkError) {
    return true
  }
  if (error instanceof ApiProblemError || error instanceof ApiResponseError) {
    return error.status >= FIRST_SERVER_ERROR_STATUS
  }
  return false
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: shouldRetryQuery },
      // Escritas nunca repetem sozinhas: o efeito pode ter acontecido no servidor (spec 8.7).
      mutations: { retry: false },
    },
  })
}
