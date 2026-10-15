import { hashKey, QueryCache, QueryClient } from '@tanstack/react-query'
import { meQueryKey } from '@/features/session/query-keys'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from '@/shared/api/api-errors'
import { isUnauthorized } from '@/shared/api/request-failure'

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
  const meQueryHash = hashKey(meQueryKey)
  const queryClient: QueryClient = new QueryClient({
    // 401 numa leitura privada significa que a sessão acabou no servidor: reconsultar a identidade
    // faz a rota protegida levar à entrada com o aviso de sessão expirada (spec 13.2 e 13.5).
    queryCache: new QueryCache({
      onError: (error, query) => {
        if (isUnauthorized(error) && query.queryHash !== meQueryHash) {
          void queryClient.invalidateQueries({ queryKey: meQueryKey, exact: true })
        }
      },
    }),
    defaultOptions: {
      queries: { retry: shouldRetryQuery },
      // Escritas nunca repetem sozinhas: o efeito pode ter acontecido no servidor (spec 8.7).
      mutations: { retry: false },
    },
  })
  return queryClient
}
