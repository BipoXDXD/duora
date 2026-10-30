import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useNavigate } from 'react-router'
import { apiClient } from '@/shared/api'
import { paths } from '@/shared/paths'
import { identityBroadcast } from './identity-broadcast'
import { signInNavigation, type LoginNotice, type LoginState } from './login-state'
import { usePrivateOperation, useSessionContextSnapshot } from './private-operation'
import { usePrivateMutation, type PrivateMutationResult } from './private-mutation'
import { meQueryKey, sessionsQueryKey } from './query-keys'
import {
  fetchMe,
  fetchMeForRevalidation,
  fetchSessions,
  logout,
  revokeSession,
  SessionRestrictionError,
  type AccountSession,
  type Me,
} from './session-api'

/**
 * Dado privado não fica em memória depois que a área autenticada desmonta (gcTime 0): ao voltar a ela,
 * pelo histórico ou por link, a tela espera o servidor em vez de mostrar o que ficou de uma sessão que pode
 * ter expirado, e sem rede não reexibe a pessoa anterior (T-14, T-21).
 */
const PRIVATE_DATA_GC_TIME_MS = 0

export function useMe(): UseQueryResult<Me> {
  useSessionContextSnapshot()
  return useQuery({
    queryKey: meQueryKey,
    queryFn: ({ signal }) => fetchMe(signal),
    gcTime: PRIVATE_DATA_GC_TIME_MS,
    structuralSharing: false,
    enabled: () => {
      const context = apiClient.sessionContext.getSnapshot()
      return !context.protected && context.pendingOwner === null
    },
  })
}

export function useSessions(userId: string): UseQueryResult<readonly AccountSession[]> {
  const privateOperation = usePrivateOperation()
  return useQuery({
    queryKey: sessionsQueryKey(userId),
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchSessions(operation, signal)),
    gcTime: PRIVATE_DATA_GC_TIME_MS,
  })
}

/**
 * Troca de identidade no navegador (T-21): todo o cache do QueryClient é descartado, as outras abas
 * são avisadas e o token antiforgery, que o ASP.NET Core liga à identidade, é renovado.
 */
export interface SessionTransitions {
  /** Depois de entrar: descarta o cache, renova o CSRF e segue para `destination`. */
  readonly signedIn: (destination: string) => Promise<void>
  /**
   * Depois de sair: vai para a entrada com o aviso e renova o CSRF. O cache é descartado pela
   * tela de entrada ao montar (useDiscardCachedIdentity), depois que a área autenticada desmontou.
   */
  readonly signedOut: (notice: LoginNotice) => Promise<void>
}

export function useSessionTransitions(): SessionTransitions {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  return {
    signedIn: async (destination: string): Promise<void> => {
      apiClient.sessionContext.protect('identity')
      identityBroadcast.announce()
      const ticket = apiClient.sessionContext.beginExplicitRevalidation()
      try {
        await queryClient.query({
          queryKey: meQueryKey,
          queryFn: () => fetchMeForRevalidation(ticket),
          structuralSharing: false,
          retry: false,
          staleTime: 0,
        })
        void navigate(destination, { replace: true, state: signInNavigation })
      } catch (error) {
        if (error instanceof SessionRestrictionError) {
          void navigate(paths.changePassword, { replace: true })
          return
        }
        throw error
      }
    },
    signedOut: (notice: LoginNotice): Promise<void> => {
      apiClient.sessionContext.protect('identity')
      void navigate(paths.login, { replace: true, state: { notice } satisfies LoginState })
      identityBroadcast.announce()
      return Promise.resolve()
    },
  }
}

/**
 * A tela de entrada nunca mostra dado privado: ao montar, descarta todo o cache (spec 13.5, T-21).
 * Limpar antes, ainda com a área autenticada montada, não serve: o React Router navega dentro de
 * uma transition, a árvore antiga continua montada por um instante e as consultas dela, sem dado
 * no cache, buscariam de novo os dados da identidade que acabou de sair.
 */
export function useDiscardCachedIdentity(): void {
  const queryClient = useQueryClient()
  useEffect(() => {
    apiClient.sessionContext.protect('identity')
    queryClient.clear()
  }, [queryClient])
}

export function useLogout(): PrivateMutationResult<void, void> {
  const transitions = useSessionTransitions()
  return usePrivateMutation({
    mutationFn: (operation) => logout(operation),
    onSuccess: () => transitions.signedOut('signed-out'),
  })
}

export function useRevokeSession(userId: string): PrivateMutationResult<void, AccountSession> {
  const queryClient = useQueryClient()
  const transitions = useSessionTransitions()
  return usePrivateMutation({
    mutationFn: (operation, session: AccountSession) => revokeSession(operation, session.id),
    onSuccess: (_result, session) =>
      session.current
        ? transitions.signedOut('signed-out')
        : queryClient.invalidateQueries({ queryKey: sessionsQueryKey(userId) }),
    // 401 aqui (sessão encerrada no servidor) é tratado para toda escrita no MutationCache (query-client.ts).
  })
}
