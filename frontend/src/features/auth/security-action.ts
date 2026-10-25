import type { QueryClient } from '@tanstack/react-query'
import { fetchMeForRevalidation } from '@/features/session/session-api'
import { meQueryKey } from '@/features/session/query-keys'
import { ApiNetworkError, ApiResponseError } from '@/shared/api'
import type { OperationHandle } from '@/shared/session-context'
import { pageSecurityResult } from './page-security-result'
import type { SecurityResult } from './security-result'

/** Não devolve segredo nem ticket ao MutationCache. O novo dono recebe o resultado só pela entrega própria. */
export async function executeSecurityAction(
  operation: OperationHandle,
  produce: () => Promise<SecurityResult>,
  cache: QueryClient,
): Promise<void> {
  let result: SecurityResult
  try {
    result = await produce()
  } catch (error) {
    if (
      error instanceof ApiNetworkError ||
      (error instanceof ApiResponseError && error.status >= 200 && error.status < 300)
    )
      pageSecurityResult.rejectReceipt(operation)
    throw error
  }
  const ticket = pageSecurityResult.stage(operation, result)
  try {
    await cache.cancelQueries({ queryKey: meQueryKey, exact: true })
    const revalidation = pageSecurityResult.bootstrap(ticket)
    await cache.query({
      queryKey: meQueryKey,
      structuralSharing: false,
      retry: false,
      staleTime: 0,
      queryFn: async () => {
        const me = await fetchMeForRevalidation(revalidation)
        // Valida o destino ANTES que o par Me/owner entre no cache e habilite qualquer ack/UI.
        pageSecurityResult.accept(ticket, me)
        return me
      },
    })
  } catch (error) {
    pageSecurityResult.fail(ticket)
    throw error
  }
}
