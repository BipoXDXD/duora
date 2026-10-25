import { createContext, useContext, useMemo, useSyncExternalStore } from 'react'
import { apiClient } from '@/shared/api'
import {
  contextAbort,
  type ContextSnapshot,
  type OperationHandle,
  type OwnerHandle,
  type SessionContextStore,
} from '@/shared/session-context'

export const PrivateContext = createContext<{
  readonly owner: OwnerHandle
  readonly store: SessionContextStore
} | null>(null)

export function usePrivateOwner(): OwnerHandle {
  const context = useContext(PrivateContext)
  if (context === null) throw new Error('A operação privada exige uma área autenticada.')
  return context.owner
}

export interface PrivateOperationPort {
  readonly store: SessionContextStore
  begin(): OperationHandle
  finish(operation: OperationHandle): void
  assertCurrent(): void
  run<T>(action: (operation: OperationHandle) => Promise<T>): Promise<T>
}

export function usePrivateOperation(): PrivateOperationPort {
  const context = useContext(PrivateContext)
  if (context === null) throw new Error('A operação privada exige uma área autenticada.')
  const { owner, store } = context
  return useMemo(
    () => ({
      store,
      begin: () => store.begin(owner),
      finish: (operation: OperationHandle) => {
        store.finish(operation)
      },
      assertCurrent: () => {
        const state = store.getSnapshot()
        if (state.protected || state.owner !== owner) throw contextAbort()
      },
      async run<T>(action: (operation: OperationHandle) => Promise<T>): Promise<T> {
        const operation = store.begin(owner)
        try {
          const result = await action(operation)
          store.assertCurrent(operation)
          return result
        } finally {
          store.finish(operation)
        }
      },
    }),
    [owner, store],
  )
}

export function useSessionContextSnapshot(store: SessionContextStore = apiClient.sessionContext): ContextSnapshot {
  return useSyncExternalStore(
    (notify) => store.subscribe(notify),
    () => store.getSnapshot(),
    () => store.getSnapshot(),
  )
}
