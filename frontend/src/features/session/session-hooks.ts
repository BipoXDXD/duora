import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query'
import { useEffect } from 'react'
import { useNavigate } from 'react-router'
import { apiClient, refreshAntiforgeryToken } from '@/shared/api'
import { paths } from '@/shared/paths'
import { identityBroadcast } from './identity-broadcast'
import type { LoginNotice, LoginState } from './login-state'
import { meQueryKey, sessionsQueryKey } from './query-keys'
import { fetchMe, fetchSessions, logout, revokeSession, type AccountSession, type Me } from './session-api'

/**
 * Dado privado não fica em memória depois que a área autenticada desmonta (gcTime 0): ao voltar a ela,
 * pelo histórico ou por link, a tela espera o servidor em vez de mostrar o que ficou de uma sessão que pode
 * ter expirado, e sem rede não reexibe a pessoa anterior (T-14, T-21).
 */
const PRIVATE_DATA_GC_TIME_MS = 0

export function useMe(): UseQueryResult<Me> {
  return useQuery({ queryKey: meQueryKey, queryFn: ({ signal }) => fetchMe(signal), gcTime: PRIVATE_DATA_GC_TIME_MS })
}

export function useSessions(userId: string): UseQueryResult<readonly AccountSession[]> {
  return useQuery({
    queryKey: sessionsQueryKey(userId),
    queryFn: ({ signal }) => fetchSessions(signal),
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
      queryClient.clear()
      identityBroadcast.announce()
      await renewAntiforgeryTokenAfterIdentityChange()
      void navigate(destination, { replace: true })
    },
    signedOut: async (notice: LoginNotice): Promise<void> => {
      void navigate(paths.login, { replace: true, state: { notice } satisfies LoginState })
      identityBroadcast.announce()
      await renewAntiforgeryTokenAfterIdentityChange()
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
    queryClient.clear()
  }, [queryClient])
}

async function renewAntiforgeryTokenAfterIdentityChange(): Promise<void> {
  try {
    await refreshAntiforgeryToken(apiClient)
  } catch {
    // Sem rede, o token fica para depois: toda mutação pede um token novo antes de ser enviada
    // (sendMutation), então nenhuma sai com o token da identidade anterior.
  }
}

export function useLogout(): UseMutationResult<void, Error, void> {
  const transitions = useSessionTransitions()
  return useMutation({
    mutationFn: logout,
    onSuccess: () => transitions.signedOut('signed-out'),
  })
}

export function useRevokeSession(userId: string): UseMutationResult<void, Error, AccountSession> {
  const queryClient = useQueryClient()
  const transitions = useSessionTransitions()
  return useMutation({
    mutationFn: (session: AccountSession) => revokeSession(session.id),
    onSuccess: (_result, session) =>
      session.current
        ? transitions.signedOut('signed-out')
        : queryClient.invalidateQueries({ queryKey: sessionsQueryKey(userId) }),
    // 401 aqui (sessão encerrada no servidor) é tratado para toda escrita no MutationCache (query-client.ts).
  })
}
