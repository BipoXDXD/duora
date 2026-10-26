import { useEffect, useMemo, type ReactNode } from 'react'
import type { QueryClient } from '@tanstack/react-query'
import { apiClient } from '@/shared/api'
import type { OwnerHandle, SessionContextStore } from '@/shared/session-context'
import { PrivateContext } from './private-operation'
import { acknowledgeOwnerInstallation, type OwnerInstallation } from './owner-installation'

export function PrivateOperationProvider({
  owner,
  store = apiClient.sessionContext,
  children,
}: {
  readonly owner: OwnerHandle
  readonly store?: SessionContextStore
  readonly children: ReactNode
}): React.JSX.Element {
  const value = useMemo(() => ({ owner, store }), [owner, store])
  return <PrivateContext value={value}>{children}</PrivateContext>
}

/** Só monta no ramo neutro; o efeito ocorre depois do commit que desmontou a árvore anterior. */
export function PendingOwnerInstallation({
  store = apiClient.sessionContext,
  queryClient,
  installation,
  children,
}: {
  readonly store?: SessionContextStore
  readonly queryClient: QueryClient
  readonly installation: OwnerInstallation
  readonly children: ReactNode
}): React.JSX.Element {
  const { epoch, owner } = installation
  useEffect(() => {
    void acknowledgeOwnerInstallation(store, queryClient, { epoch, owner }).catch((error: unknown) => {
      if (error instanceof DOMException && error.name === 'AbortError') return
      const current = store.getSnapshot()
      if (current.transitionEpoch === epoch && current.pendingOwner === owner) store.protect('context')
    })
  }, [store, queryClient, epoch, owner])
  return <>{children}</>
}
