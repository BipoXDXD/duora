import { apiClient } from '@/shared/api'
import type { OperationHandle } from '@/shared/session-context'
import { useOwnedMutation, type OwnedMutationOptions, type PrivateMutationResult } from './private-mutation'

export function usePublicMutation<TData, TVariables = void>(
  options: OwnedMutationOptions<TData, TVariables>,
): PrivateMutationResult<TData, TVariables> {
  return useOwnedMutation(options, {
    store: apiClient.sessionContext,
    begin: () => apiClient.sessionContext.beginPublic(),
  })
}

/** O gesto começa antes do resolver; o mesmo snapshot chega ao CSRF, POST e callbacks. */
export async function runPublicOperation<T>(action: (operation: OperationHandle) => Promise<T>): Promise<T> {
  const store = apiClient.sessionContext
  const operation = store.beginPublic()
  try {
    const result = await action(operation)
    store.assertCurrent(operation)
    return result
  } finally {
    store.finish(operation)
  }
}
