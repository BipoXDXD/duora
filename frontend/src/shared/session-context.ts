export const ANTIFORGERY_HEADER_NAME = 'X-XSRF-TOKEN'

declare const ownerBrand: unique symbol
declare const operationBrand: unique symbol
declare const bootstrapBrand: unique symbol
declare const revalidationBrand: unique symbol

class OwnerToken {
  declare readonly [ownerBrand]: true
}
class OperationToken {
  declare readonly [operationBrand]: true
}
class BootstrapToken {
  declare readonly [bootstrapBrand]: true
}
class RevalidationToken {
  declare readonly [revalidationBrand]: true
}

export type OwnerHandle = OwnerToken
export type OperationHandle = OperationToken
export type BootstrapTicket = BootstrapToken
export type RevalidationTicket = RevalidationToken
export type SessionPhase = 'active' | 'mfa-enrollment' | 'password-change'

export interface ValidatedBootstrap {
  readonly userId: string
  readonly sessionId: string
  readonly phase: SessionPhase
  readonly requestToken: string
  readonly authView: {
    readonly status: string
    readonly emailConfirmed: boolean
    readonly mfaEnabled: boolean
    readonly roles: readonly string[]
  } | null
}

export interface ContextSnapshot {
  readonly protected: boolean
  readonly owner: OwnerHandle | null
  readonly pendingOwner: OwnerHandle | null
  readonly transitionEpoch: number
}

export type BootstrapSource =
  { readonly kind: 'automatic' } | { readonly kind: 'explicit'; readonly ticket: RevalidationTicket }

export interface SessionContextStore {
  beginBootstrap(source: BootstrapSource): BootstrapTicket
  assertBootstrap(ticket: BootstrapTicket): void
  finishBootstrap(ticket: BootstrapTicket): void
  acceptBootstrap(ticket: BootstrapTicket, value: ValidatedBootstrap): OwnerHandle
  acceptAnonymous(ticket: BootstrapTicket): void
  completeOwnerInstallation(epoch: number, owner: OwnerHandle): void
  begin(owner: OwnerHandle): OperationHandle
  beginPublic(): OperationHandle
  assertCurrent(operation: OperationHandle): void
  finish(operation: OperationHandle): void
  protect(reason: 'identity' | 'context' | 'security-transition'): void
  beginExplicitRevalidation(): RevalidationTicket
  subscribe(listener: () => void): () => void
  getSnapshot(): ContextSnapshot
}

interface OwnerRecord {
  readonly handle: OwnerHandle
  readonly identity: string
  readonly binding: Pick<ValidatedBootstrap, 'userId' | 'sessionId' | 'phase'>
  proof: string
  readonly lifetime: AbortController
}

interface OperationRecord {
  readonly kind: 'private' | 'public'
  readonly owner: OwnerHandle | null
  readonly epoch: number
  readonly proof: string | null
  readonly controller: AbortController
  readonly cleanup: Set<() => void>
}

interface BootstrapRecord {
  readonly epoch: number
  readonly sequence: number
  readonly explicit: RevalidationTicket | null
  accepted: boolean
  bound: boolean
  readonly controller: AbortController
  readonly cleanup: Set<() => void>
}

interface InternalPort {
  owner(handle: OwnerHandle): OwnerRecord | undefined
  operation(handle: OperationHandle): OperationRecord
  bootstrap(ticket: BootstrapTicket): BootstrapRecord
}

const ports = new WeakMap<SessionContextStore, InternalPort>()

export function contextAbort(): DOMException {
  return new DOMException('O contexto desta operação já não está disponível.', 'AbortError')
}

function fingerprint(value: ValidatedBootstrap): string {
  const view = value.authView
  return JSON.stringify([
    value.userId.toLowerCase(),
    value.sessionId.toLowerCase(),
    value.phase,
    view === null ? null : [view.status, view.emailConfirmed, view.mfaEnabled, [...new Set(view.roles)].sort()],
  ])
}

/** Provas e identidades ficam nos WeakMaps, nunca nos handles serializáveis do cache. */
export function createSessionContextStore(): SessionContextStore {
  const owners = new WeakMap<OwnerHandle, OwnerRecord>()
  const operations = new WeakMap<OperationHandle, OperationRecord>()
  const activeOperations = new Set<OperationRecord>()
  const bootstraps = new WeakMap<BootstrapTicket, BootstrapRecord>()
  const activeBootstraps = new Set<BootstrapRecord>()
  const revalidations = new WeakMap<RevalidationTicket, { readonly epoch: number; started: boolean }>()
  const listeners = new Set<() => void>()
  let epoch = 0
  let sequence = 0
  let acceptedSequence = 0
  let explicit: RevalidationTicket | null = null
  let snapshot: ContextSnapshot = Object.freeze({
    protected: false,
    owner: null,
    pendingOwner: null,
    transitionEpoch: 0,
  })

  const notify = (): void => {
    for (const listener of [...listeners]) listener()
  }
  const update = (value: Omit<ContextSnapshot, 'transitionEpoch'>): void => {
    snapshot = Object.freeze({ ...value, transitionEpoch: epoch })
    notify()
  }
  const disposeOperation = (record: OperationRecord): void => {
    record.controller.abort()
    for (const cleanup of record.cleanup) cleanup()
    record.cleanup.clear()
    activeOperations.delete(record)
  }
  const invalidate = (): void => {
    epoch += 1
    for (const owner of [snapshot.owner, snapshot.pendingOwner]) {
      if (owner !== null) owners.get(owner)?.lifetime.abort()
    }
    for (const record of [...activeOperations]) disposeOperation(record)
    for (const record of [...activeBootstraps]) {
      record.controller.abort()
      for (const cleanup of record.cleanup) cleanup()
      record.cleanup.clear()
      activeBootstraps.delete(record)
    }
    explicit = null
  }
  const assertBootstrap = (ticket: BootstrapTicket): BootstrapRecord => {
    const record = bootstraps.get(ticket)
    if (
      record === undefined ||
      record.accepted ||
      record.controller.signal.aborted ||
      record.epoch !== epoch ||
      record.sequence < acceptedSequence ||
      (record.explicit !== null && record.explicit !== explicit) ||
      (snapshot.protected && record.explicit === null)
    ) {
      throw contextAbort()
    }
    return record
  }
  const operationRecord = (handle: OperationHandle): OperationRecord => {
    const record = operations.get(handle)
    if (
      record?.epoch !== epoch ||
      record.controller.signal.aborted ||
      (record.kind === 'private' && (snapshot.protected || snapshot.owner !== record.owner))
    ) {
      throw contextAbort()
    }
    return record
  }
  const beginOperation = (owner: OwnerHandle | null): OperationHandle => {
    if (owner !== null && (snapshot.protected || snapshot.owner !== owner)) throw contextAbort()
    const handle = Object.freeze(new OperationToken())
    const ownerRecord = owner === null ? undefined : owners.get(owner)
    if (owner !== null && ownerRecord === undefined) throw contextAbort()
    const record: OperationRecord = {
      kind: owner === null ? 'public' : 'private',
      owner,
      epoch,
      proof: ownerRecord?.proof ?? null,
      controller: new AbortController(),
      cleanup: new Set(),
    }
    operations.set(handle, record)
    activeOperations.add(record)
    return handle
  }

  const store: SessionContextStore = {
    beginBootstrap(source) {
      const ticket = source.kind === 'explicit' ? source.ticket : null
      const authorization = ticket === null ? undefined : revalidations.get(ticket)
      if (
        (source.kind === 'automatic' && snapshot.protected) ||
        (ticket !== null && (ticket !== explicit || authorization?.epoch !== epoch || authorization.started))
      ) {
        throw contextAbort()
      }
      if (authorization !== undefined) authorization.started = true
      const handle = Object.freeze(new BootstrapToken())
      const record: BootstrapRecord = {
        epoch,
        sequence: ++sequence,
        explicit: ticket,
        accepted: false,
        bound: false,
        controller: new AbortController(),
        cleanup: new Set(),
      }
      bootstraps.set(handle, record)
      activeBootstraps.add(record)
      return handle
    },
    assertBootstrap(ticket) {
      assertBootstrap(ticket)
    },
    finishBootstrap(ticket) {
      const record = bootstraps.get(ticket)
      if (record !== undefined) {
        record.controller.abort()
        for (const cleanup of record.cleanup) cleanup()
        activeBootstraps.delete(record)
      }
      bootstraps.delete(ticket)
    },
    acceptBootstrap(ticket, value) {
      const record = assertBootstrap(ticket)
      const identity = fingerprint(value)
      const previous = snapshot.owner ?? snapshot.pendingOwner
      const previousRecord = previous === null ? undefined : owners.get(previous)
      record.accepted = true
      activeBootstraps.delete(record)
      acceptedSequence = record.sequence
      if (previousRecord?.identity === identity && !snapshot.protected) {
        previousRecord.proof = value.requestToken
        return previousRecord.handle
      }
      const wasProtected = snapshot.protected
      if (previous !== null || activeOperations.size > 0) invalidate()
      const owner = Object.freeze(new OwnerToken())
      owners.set(owner, {
        handle: owner,
        identity,
        binding: { userId: value.userId, sessionId: value.sessionId, phase: value.phase },
        proof: value.requestToken,
        lifetime: new AbortController(),
      })
      update({ protected: wasProtected, owner: null, pendingOwner: owner })
      if (snapshot.pendingOwner !== owner) throw contextAbort()
      return owner
    },
    acceptAnonymous(ticket) {
      const record = assertBootstrap(ticket)
      record.accepted = true
      activeBootstraps.delete(record)
      acceptedSequence = record.sequence
      invalidate()
      update({ protected: false, owner: null, pendingOwner: null })
    },
    completeOwnerInstallation(expectedEpoch, owner) {
      if (
        expectedEpoch !== epoch ||
        snapshot.pendingOwner !== owner ||
        owners.get(owner)?.lifetime.signal.aborted !== false
      ) {
        throw contextAbort()
      }
      explicit = null
      update({ protected: false, owner, pendingOwner: null })
    },
    begin(owner) {
      return beginOperation(owner)
    },
    beginPublic() {
      return beginOperation(null)
    },
    assertCurrent(operation) {
      operationRecord(operation)
    },
    finish(operation) {
      const record = operations.get(operation)
      if (record !== undefined) disposeOperation(record)
      operations.delete(operation)
    },
    protect() {
      invalidate()
      update({ protected: true, owner: null, pendingOwner: null })
    },
    beginExplicitRevalidation() {
      invalidate()
      const capturedEpoch = epoch
      const ticket = Object.freeze(new RevalidationToken())
      revalidations.set(ticket, { epoch, started: false })
      explicit = ticket
      update({ protected: true, owner: null, pendingOwner: null })
      if (epoch !== capturedEpoch || explicit !== ticket) throw contextAbort()
      return ticket
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    getSnapshot() {
      return snapshot
    },
  }
  ports.set(store, { owner: (handle) => owners.get(handle), operation: operationRecord, bootstrap: assertBootstrap })
  return store
}

function bindRequest(
  store: SessionContextStore,
  operation: OperationHandle,
  binding: { readonly request: Request; readonly kind: OperationRecord['kind']; readonly headers?: HeadersInit },
): Request {
  const { request, kind, headers: policyHeaders } = binding
  const record = ports.get(store)?.operation(operation)
  if (record?.kind !== kind) throw contextAbort()
  const abort = (): void => {
    record.controller.abort()
  }
  if (request.signal.aborted) abort()
  else {
    request.signal.addEventListener('abort', abort, { once: true })
    record.cleanup.add(() => {
      request.signal.removeEventListener('abort', abort)
    })
  }
  store.assertCurrent(operation)
  const headers = new Headers(policyHeaders ?? request.headers)
  if (kind === 'private') {
    if (record.proof === null) throw contextAbort()
    headers.set(ANTIFORGERY_HEADER_NAME, record.proof)
  }
  return new Request(request, {
    headers,
    credentials: 'same-origin',
    signal: record.controller.signal,
    ...(kind === 'private' ? { redirect: 'error' as const } : {}),
  })
}

/** Único adaptador da prova: produz a requisição vinculada, não um getter público de token. */
export function bindPrivateRequest(
  store: SessionContextStore,
  operation: OperationHandle,
  binding: Request | { readonly request: Request; readonly headers: HeadersInit },
): Request {
  return bindRequest(store, operation, {
    ...(binding instanceof Request ? { request: binding } : binding),
    kind: 'private',
  })
}

export function bindPublicRequest(
  store: SessionContextStore,
  operation: OperationHandle,
  binding: Request | { readonly request: Request; readonly headers: HeadersInit },
): Request {
  return bindRequest(store, operation, {
    ...(binding instanceof Request ? { request: binding } : binding),
    kind: 'public',
  })
}

export function bindBootstrapRequest(
  store: SessionContextStore,
  ticket: BootstrapTicket,
  binding: { readonly request: Request; readonly headers: HeadersInit },
): Request {
  const { request, headers } = binding
  const record = ports.get(store)?.bootstrap(ticket)
  if (record === undefined || record.bound) throw contextAbort()
  record.bound = true
  const abort = (): void => {
    record.controller.abort()
  }
  if (request.signal.aborted) abort()
  else {
    request.signal.addEventListener('abort', abort, { once: true })
    record.cleanup.add(() => {
      request.signal.removeEventListener('abort', abort)
    })
  }
  store.assertBootstrap(ticket)
  return new Request(request, {
    signal: record.controller.signal,
    redirect: 'error',
    credentials: 'same-origin',
    headers,
  })
}

/** A transição recebe só uma referência opaca do snapshot original já validado. */
export function captureOperationOwner(store: SessionContextStore, operation: OperationHandle): OwnerHandle {
  const record = ports.get(store)?.operation(operation)
  if (record?.kind !== 'private' || record.owner === null) throw contextAbort()
  return record.owner
}

/** Correlação restrita de resultado: retorna apenas sim/não, nunca prova ou SID do WeakMap. */
export function matchesSecurityResultOwner(
  store: SessionContextStore,
  source: OwnerHandle,
  target: { readonly owner: OwnerHandle; readonly sessionId?: string },
): boolean {
  const port = ports.get(store)
  const original = port?.owner(source)?.binding
  const candidate = port?.owner(target.owner)?.binding
  if (original === undefined || candidate === undefined) return false
  return (
    original.userId.toLowerCase() === candidate.userId.toLowerCase() &&
    (target.sessionId ?? original.sessionId).toLowerCase() === candidate.sessionId.toLowerCase() &&
    (target.sessionId === undefined ? original.phase === candidate.phase : candidate.phase === 'active')
  )
}
