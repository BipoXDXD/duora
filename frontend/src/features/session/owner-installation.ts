import { hashKey, type QueryClient } from '@tanstack/react-query'
import { contextAbort, type OwnerHandle, type SessionContextStore } from '@/shared/session-context'
import { meQueryKey } from './query-keys'

export interface OwnerInstallation {
  readonly epoch: number
  readonly owner: OwnerHandle
}

export async function acknowledgeOwnerInstallation(
  store: SessionContextStore,
  queryClient: QueryClient,
  installation: OwnerInstallation,
): Promise<void> {
  const assertPending = (): void => {
    const state = store.getSnapshot()
    if (state.transitionEpoch !== installation.epoch || state.pendingOwner !== installation.owner) throw contextAbort()
  }
  assertPending()
  const meHash = hashKey(meQueryKey)
  await queryClient.cancelQueries({ predicate: (query) => query.queryHash !== meHash })
  assertPending()
  queryClient.removeQueries({ predicate: (query) => query.queryHash !== meHash })
  queryClient.getMutationCache().clear()
  assertPending()
  store.completeOwnerInstallation(installation.epoch, installation.owner)
}
