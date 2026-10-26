import { hashKey, MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { meQueryKey } from '@/features/session/query-keys'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from '@/shared/api/api-errors'
import { pageSessionContext } from '@/shared/page-session-context'
import type { SessionContextStore } from '@/shared/session-context'
import { isSessionRestriction, isUnauthorized } from '@/shared/api/request-failure'

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

export function createQueryClient(context: SessionContextStore = pageSessionContext): QueryClient {
  const meQueryHash = hashKey(meQueryKey)
  const recheckIdentity = (): void => {
    if (context.getSnapshot().protected || context.getSnapshot().pendingOwner !== null) return
    void queryClient.invalidateQueries({ queryKey: meQueryKey, exact: true })
  }
  const queryClient: QueryClient = new QueryClient({
    // Só /me401 decide a saída. Recusas conhecidas de credencial em comandos já autorizados
    // conservam a sessão/formulário; o par cookie/prova foi validado antes do handler.
    // Outros401 e restrições verificam a identidade, sem refetch se já houver proteção.
    queryCache: new QueryCache({
      onError: (error, query) => {
        if ((isUnauthorized(error) || isSessionRestriction(error)) && query.queryHash !== meQueryHash) {
          recheckIdentity()
        }
      },
    }),
    mutationCache: new MutationCache({
      onError: (error) => {
        const credentialRejection =
          error instanceof ApiProblemError &&
          error.status === 401 &&
          (error.problem.code === 'INVALID_CREDENTIALS' || error.problem.code === 'REAUTHENTICATION_REQUIRED')
        if (!credentialRejection && (isUnauthorized(error) || isSessionRestriction(error))) {
          recheckIdentity()
        }
      },
    }),
    defaultOptions: {
      queries: {
        retry: (failureCount, error) => !context.getSnapshot().protected && shouldRetryQuery(failureCount, error),
      },
      // Escritas nunca repetem sozinhas: o efeito pode ter acontecido no servidor (spec 8.7).
      mutations: { retry: false },
    },
  })
  queryClient.setQueryDefaults(meQueryKey, { structuralSharing: false })
  return queryClient
}
