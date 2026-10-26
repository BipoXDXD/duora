import {
  captureOperationOwner,
  contextAbort,
  matchesSecurityResultOwner,
  type OperationHandle,
  type OwnerHandle,
  type RevalidationTicket,
  type SessionContextStore,
} from '@/shared/session-context'

declare const resultBrand: unique symbol
class ResultToken {
  declare readonly [resultBrand]: true
}
export type SecurityResultTicket = ResultToken
export type SecurityResult =
  | { readonly kind: 'mfa-enrollment'; readonly sharedKey: string; readonly authenticatorUri: string }
  | { readonly kind: 'recovery-codes'; readonly codes: readonly string[]; readonly resultSessionId: string }
export type OwnedSecurityResult =
  | { readonly kind: 'mfa-enrollment'; readonly sharedKey: string; readonly authenticatorUri: string }
  | { readonly kind: 'recovery-codes'; readonly codes: readonly string[] }
export interface SecurityResultSnapshot {
  readonly phase: 'idle' | 'pending' | 'ready' | 'failed'
}
export interface SecurityResultStore {
  stage(operation: OperationHandle, result: SecurityResult): SecurityResultTicket
  bootstrap(ticket: SecurityResultTicket): RevalidationTicket
  accept(ticket: SecurityResultTicket, me: { readonly owner: OwnerHandle; readonly mfaEnabled: boolean }): void
  forOwner(owner: OwnerHandle): OwnedSecurityResult | null
  fail(ticket: SecurityResultTicket): void
  rejectReceipt(operation: OperationHandle): void
  discard(): void
  subscribe(listener: () => void): () => void
  getSnapshot(): SecurityResultSnapshot
}
interface PendingResult {
  readonly ticket: SecurityResultTicket
  readonly revalidation: RevalidationTicket
  readonly epoch: number
  readonly source: OwnerHandle
  readonly payload: OwnedSecurityResult
  readonly expectedSessionId: string | null
  confirmedOwner: OwnerHandle | null
}

/** Um único resultado fora dos caches; a entrada própria é atômica, toda proteção posterior o descarta. */
export function createSecurityResultStore(context: SessionContextStore): SecurityResultStore {
  let current: PendingResult | null = null
  let snapshot: SecurityResultSnapshot = Object.freeze({ phase: 'idle' })
  const listeners = new Set<() => void>()
  const publish = (phase: SecurityResultSnapshot['phase']): void => {
    snapshot = Object.freeze({ phase })
    for (const listener of [...listeners]) listener()
  }
  const fail = (): void => {
    current = null
    publish('failed')
  }
  const requireCurrent = (ticket: SecurityResultTicket): PendingResult => {
    if (current?.ticket !== ticket || current.epoch !== context.getSnapshot().transitionEpoch) throw contextAbort()
    return current
  }
  context.subscribe(() => {
    if (current !== null && current.epoch !== context.getSnapshot().transitionEpoch) fail()
  })
  return {
    stage(operation, result) {
      const source = captureOperationOwner(context, operation)
      if (current !== null) fail()
      const expectedEpoch = context.getSnapshot().transitionEpoch + 1
      // Entrada única: invalida antes do await e só transfere o resultado se nenhuma proteção reentrante a substituiu.
      let revalidation: RevalidationTicket
      try {
        revalidation = context.beginExplicitRevalidation()
      } catch (error) {
        fail()
        throw error
      }
      const state = context.getSnapshot()
      if (
        state.transitionEpoch !== expectedEpoch ||
        !state.protected ||
        state.owner !== null ||
        state.pendingOwner !== null
      ) {
        fail()
        throw contextAbort()
      }
      const ticket = Object.freeze(new ResultToken())
      const payload: OwnedSecurityResult =
        result.kind === 'mfa-enrollment'
          ? Object.freeze({ kind: result.kind, sharedKey: result.sharedKey, authenticatorUri: result.authenticatorUri })
          : Object.freeze({ kind: result.kind, codes: Object.freeze([...result.codes]) })
      current = {
        ticket,
        revalidation,
        epoch: expectedEpoch,
        source,
        payload,
        expectedSessionId: result.kind === 'recovery-codes' ? result.resultSessionId : null,
        confirmedOwner: null,
      }
      publish('pending')
      requireCurrent(ticket)
      return ticket
    },
    bootstrap(ticket) {
      return requireCurrent(ticket).revalidation
    },
    accept(ticket, me) {
      const record = requireCurrent(ticket)
      const state = context.getSnapshot()
      if (
        (state.pendingOwner !== me.owner && state.owner !== me.owner) ||
        (record.payload.kind === 'recovery-codes' && !me.mfaEnabled) ||
        !matchesSecurityResultOwner(context, record.source, {
          owner: me.owner,
          ...(record.expectedSessionId === null ? {} : { sessionId: record.expectedSessionId }),
        })
      ) {
        fail()
        context.protect('security-transition')
        throw new Error('security-result-owner')
      }
      record.confirmedOwner = me.owner
      publish('ready')
    },
    forOwner(owner) {
      if (
        current?.confirmedOwner !== owner ||
        current.epoch !== context.getSnapshot().transitionEpoch ||
        context.getSnapshot().owner !== owner ||
        context.getSnapshot().protected
      )
        return null
      return current.payload
    },
    fail(ticket) {
      if (current?.ticket === ticket) {
        fail()
        context.protect('security-transition')
      }
    },
    rejectReceipt(operation) {
      try {
        captureOperationOwner(context, operation)
      } catch {
        return
      }
      current = null
      context.protect('security-transition')
      publish('failed')
    },
    discard() {
      current = null
      publish('idle')
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    getSnapshot: () => snapshot,
  }
}
