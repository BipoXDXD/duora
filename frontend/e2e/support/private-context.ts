export const SYNTHETIC_SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-000000000099'
export const SYNTHETIC_RENEWED_SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-000000000100'
interface SyntheticAccount {
  readonly id: string
  readonly status: string
  readonly emailConfirmed: boolean
  readonly mfaEnabled: boolean
  readonly roles: readonly string[]
}
interface SyntheticContext {
  readonly userId: string
  readonly sessionId: string
  readonly phase: string
  readonly requestToken: string
}

/** Explicit positive fixture only; malformed/missing bootstrap tests do not call it. */
export function withSessionContext<T extends SyntheticAccount>(
  account: T,
  sessionId = SYNTHETIC_SESSION_ID,
): T & { readonly currentTermsAccepted: boolean; readonly sessionContext: SyntheticContext } {
  // O servidor sempre informa o aceite dos Termos; a fixture positiva parte de uma conta em dia.
  return {
    currentTermsAccepted: true,
    ...account,
    sessionContext: {
      userId: account.id,
      sessionId,
      requestToken: 'synthetic',
      phase: !account.mfaEnabled && account.roles.some((role) => role !== 'participant') ? 'mfa-enrollment' : 'active',
    },
  }
}

export function passwordChangeBootstrap(userId: string): {
  readonly status: number
  readonly code: string
  readonly sessionContext: SyntheticContext
} {
  return {
    status: 403,
    code: 'PASSWORD_CHANGE_REQUIRED',
    sessionContext: { userId, sessionId: SYNTHETIC_SESSION_ID, phase: 'password-change', requestToken: 'synthetic' },
  }
}
