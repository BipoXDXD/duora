import type { OperationHandle, SessionContextStore } from '@/shared/session-context'

export const SYNTHETIC_PRIVATE_TEST_PROOF = 'synthetic-private-test-proof'

/** Contexto explícito para testes isolados de transporte, sem depender do cliente DOM nem rebootstrap por request. */
export function syntheticPrivateOperation(store: SessionContextStore): OperationHandle {
  const ticket = store.beginBootstrap({ kind: 'automatic' })
  const owner = store.acceptBootstrap(ticket, {
    userId: '00000000-0000-4000-8000-000000000001',
    sessionId: '00000000-0000-4000-8000-000000000011',
    phase: 'active',
    requestToken: SYNTHETIC_PRIVATE_TEST_PROOF,
    authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] },
  })
  store.finishBootstrap(ticket)
  store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  return store.begin(owner)
}
