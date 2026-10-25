import { useSyncExternalStore } from 'react'
import { usePrivateOwner } from '@/features/session/private-operation'
import { pageSecurityResult } from './page-security-result'
import type { OwnedSecurityResult, SecurityResultSnapshot } from './security-result'

export function useSecurityResultSnapshot(): SecurityResultSnapshot {
  return useSyncExternalStore(
    (notify) => pageSecurityResult.subscribe(notify),
    () => pageSecurityResult.getSnapshot(),
    () => pageSecurityResult.getSnapshot(),
  )
}

export function useOwnedSecurityResult(): OwnedSecurityResult | null {
  useSecurityResultSnapshot()
  const owner = usePrivateOwner()
  return pageSecurityResult.forOwner(owner)
}
