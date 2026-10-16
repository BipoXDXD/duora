import { hashKey, MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
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
  let lastUserId: string | undefined
  const recheckIdentity = (): void => {
    void queryClient.invalidateQueries({ queryKey: meQueryKey, exact: true })
  }
  const queryClient: QueryClient = new QueryClient({
    // 401 numa leitura ou escrita privada significa que a sessão acabou no servidor: reconsultar a identidade
    // faz a rota protegida levar à entrada com o aviso de sessão expirada (spec 13.2 e 13.5; T-14).
    queryCache: new QueryCache({
      onError: (error, query) => {
        if (isUnauthorized(error) && query.queryHash !== meQueryHash) {
          recheckIdentity()
        }
      },
      // Outra aba pode ter trocado o cookie de identidade: se /me responde outra pessoa, nada da anterior fica
      // em memória (T-21). Só a identidade sobrevive, porque já é a nova.
      onSuccess: (data, query) => {
        if (query.queryHash !== meQueryHash) {
          return
        }
        const userId = readUserId(data)
        if (lastUserId !== undefined && userId !== lastUserId) {
          queryClient.removeQueries({ predicate: (cached) => cached.queryHash !== meQueryHash })
        }
        lastUserId = userId
      },
    }),
    mutationCache: new MutationCache({
      onError: (error) => {
        if (isUnauthorized(error)) {
          recheckIdentity()
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

function readUserId(me: unknown): string | undefined {
  if (typeof me === 'object' && me !== null && 'id' in me && typeof me.id === 'string') {
    return me.id
  }
  return undefined
}
