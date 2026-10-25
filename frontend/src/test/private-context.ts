import { HttpResponse } from 'msw'
import { pageSecurityResult } from '@/features/auth/page-security-result'
import { apiClient } from '@/shared/api'
import type { Me } from '@/features/session/session-api'
import type { SessionPhase, OperationHandle } from '@/shared/session-context'

export const TEST_SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-000000000099'
export const TEST_PRIVATE_PROOF = 'synthetic-private-proof'
interface SyntheticAccount {
  readonly id: string
  readonly mfaEnabled: boolean
  readonly roles: readonly string[]
  readonly status: string
  readonly emailConfirmed: boolean
}

/** Chamado explicitamente só por fixtures positivas de Me; nunca modifica respostas MSW globalmente. */
export function withSessionContext<T extends SyntheticAccount>(
  account: T,
  sessionId = TEST_SESSION_ID,
): T & {
  readonly sessionContext: {
    readonly userId: string
    readonly sessionId: string
    readonly phase: SessionPhase
    readonly requestToken: string
  }
} {
  const phase: SessionPhase =
    !account.mfaEnabled && account.roles.some((role) => role !== 'participant') ? 'mfa-enrollment' : 'active'
  return { ...account, sessionContext: { userId: account.id, sessionId, phase, requestToken: TEST_PRIVATE_PROOF } }
}

/** Nova página sintética por teste, usando a mesma confirmação anônima do protocolo real. */
export function resetTestSessionContext(): void {
  const store = apiClient.sessionContext
  pageSecurityResult.discard()
  store.protect('identity')
  const ticket = store.beginBootstrap({ kind: 'explicit', ticket: store.beginExplicitRevalidation() })
  store.acceptAnonymous(ticket)
  store.finishBootstrap(ticket)
}

/** Componentes unitários isolados reconhecem explicitamente a barreira fora de RequireSession. */
export function installTestMe(account: Omit<Me, 'owner'>): Me {
  const store = apiClient.sessionContext
  const source = store.getSnapshot().protected
    ? { kind: 'explicit' as const, ticket: store.beginExplicitRevalidation() }
    : { kind: 'automatic' as const }
  const ticket = store.beginBootstrap(source)
  const binding = withSessionContext(account).sessionContext
  const owner = store.acceptBootstrap(ticket, {
    ...binding,
    authView: {
      status: account.status,
      emailConfirmed: account.emailConfirmed,
      mfaEnabled: account.mfaEnabled,
      roles: account.roles,
    },
  })
  store.finishBootstrap(ticket)
  if (store.getSnapshot().pendingOwner === owner)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  return { ...account, owner }
}

export function testPrivateOperation(): OperationHandle {
  const me = installTestMe({
    id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
    email: 'synthetic@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
  })
  return apiClient.sessionContext.begin(me.owner)
}

export const TEST_RENEWED_SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-000000000100'
export function passwordChangeProblem(userId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'): HttpResponse<string> {
  return new HttpResponse(
    JSON.stringify({
      status: 403,
      code: 'PASSWORD_CHANGE_REQUIRED',
      sessionContext: {
        userId,
        sessionId: TEST_SESSION_ID,
        phase: 'password-change',
        requestToken: TEST_PRIVATE_PROOF,
      },
    }),
    { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
  )
}
