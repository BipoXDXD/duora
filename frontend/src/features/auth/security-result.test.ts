// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createSessionContextStore, type ValidatedBootstrap } from '@/shared/session-context'
import { createSecurityResultStore, type SecurityResultTicket } from './security-result'

const USER = '00000000-0000-4000-8000-000000000001'
const SID = '00000000-0000-4000-8000-000000000011'
const NEW_SID = '00000000-0000-4000-8000-000000000012'
const ORIGINAL: ValidatedBootstrap = {
  userId: USER,
  sessionId: SID,
  phase: 'mfa-enrollment',
  requestToken: 'proof-secret',
  authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['operator'] },
}
function fixture() {
  const context = createSessionContextStore()
  const owner = context.acceptBootstrap(context.beginBootstrap({ kind: 'automatic' }), ORIGINAL)
  context.completeOwnerInstallation(context.getSnapshot().transitionEpoch, owner)
  return { context, owner, results: createSecurityResultStore(context) }
}
function confirm(
  current: ReturnType<typeof fixture>,
  ticket: SecurityResultTicket,
  overrides: Partial<ValidatedBootstrap> = {},
) {
  const owner = current.context.acceptBootstrap(
    current.context.beginBootstrap({ kind: 'explicit', ticket: current.results.bootstrap(ticket) }),
    {
      ...ORIGINAL,
      sessionId: NEW_SID,
      phase: 'active',
      authView: { ...ORIGINAL.authView, status: 'Active', emailConfirmed: true, mfaEnabled: true, roles: ['operator'] },
      ...overrides,
    },
  )
  return owner
}
function codes(current: ReturnType<typeof fixture>, sid = NEW_SID) {
  return current.results.stage(current.context.begin(current.owner), {
    kind: 'recovery-codes',
    resultSessionId: sid,
    codes: ['one-time-secret'],
  })
}

describe('entrega exclusiva de resultado de segurança', () => {
  it('a própria entrada protege atomicamente sem perder o resultado, só visível depois do ack', () => {
    const current = fixture()
    const ticket = codes(current)
    expect(current.context.getSnapshot()).toMatchObject({ protected: true, owner: null, pendingOwner: null })
    expect(current.results.forOwner(current.owner)).toBeNull()
    expect(current.results.getSnapshot().phase).toBe('pending')
    const owner = confirm(current, ticket)
    current.results.accept(ticket, { owner, mfaEnabled: true })
    expect(current.results.forOwner(owner)).toBeNull()
    current.context.completeOwnerInstallation(current.context.getSnapshot().transitionEpoch, owner)
    expect(current.results.forOwner(owner)).toEqual({ kind: 'recovery-codes', codes: ['one-time-secret'] })
    expect(JSON.stringify({ ticket, state: current.results.getSnapshot() })).not.toContain('one-time-secret')
    expect(JSON.stringify(current.results.getSnapshot())).not.toContain(NEW_SID)
  })

  it.each([
    { userId: '00000000-0000-4000-8000-000000000002' },
    { sessionId: '00000000-0000-4000-8000-000000000013' },
    { phase: 'mfa-enrollment' as const },
  ])('mesma pessoa/SID/fase precisa corresponder exatamente ao resultado:%j', (binding) => {
    const current = fixture()
    const ticket = codes(current)
    const owner = confirm(current, ticket, binding)
    expect(() => {
      current.results.accept(ticket, { owner, mfaEnabled: true })
    }).toThrow('security-result-owner')
    expect(current.results.forOwner(owner)).toBeNull()
    expect(current.results.getSnapshot().phase).toBe('failed')
  })

  it('proteção externa posterior apaga o resultado e a leitura posterior não o restaura', () => {
    const current = fixture()
    const ticket = codes(current)
    current.context.protect('identity')
    expect(current.results.getSnapshot().phase).toBe('failed')
    expect(() => current.results.bootstrap(ticket)).toThrow(expect.objectContaining({ name: 'AbortError' }))
    expect(current.results.forOwner(current.owner)).toBeNull()
  })

  it('outro ticket explícito também apaga o resultado, inclusive depois da ativação', () => {
    const current = fixture()
    const ticket = codes(current)
    const owner = confirm(current, ticket)
    current.results.accept(ticket, { owner, mfaEnabled: true })
    current.context.completeOwnerInstallation(current.context.getSnapshot().transitionEpoch, owner)
    current.context.beginExplicitRevalidation()
    expect(current.results.getSnapshot().phase).toBe('failed')
    expect(current.results.forOwner(owner)).toBeNull()
  })

  it('resposta automática anterior não pode completar a transição nem revalidar escondido', () => {
    const current = fixture()
    const earlier = current.context.beginBootstrap({ kind: 'automatic' })
    codes(current)
    expect(() => current.context.acceptBootstrap(earlier, ORIGINAL)).toThrow()
    expect(() => current.context.beginBootstrap({ kind: 'automatic' })).toThrow()
    expect(current.results.getSnapshot().phase).toBe('pending')
  })

  it('duas ações antigas não adotam resultado uma da outra', () => {
    const current = fixture()
    const other = current.context.begin(current.owner)
    const ticket = codes(current)
    expect(() =>
      current.results.stage(other, { kind: 'recovery-codes', resultSessionId: NEW_SID, codes: ['other-secret'] }),
    ).toThrow()
    expect(current.results.bootstrap(ticket)).toBeDefined()
    expect(current.results.getSnapshot().phase).toBe('pending')
  })

  it('início/reinício de MFA conserva SID/fase e só o resultado atual é exibido', () => {
    const current = fixture()
    const ticket = current.results.stage(current.context.begin(current.owner), {
      kind: 'mfa-enrollment',
      sharedKey: 'new-shared-key',
      authenticatorUri: 'otpauth://synthetic',
    })
    const owner = confirm(current, ticket, ORIGINAL)
    current.results.accept(ticket, { owner, mfaEnabled: false })
    current.context.completeOwnerInstallation(current.context.getSnapshot().transitionEpoch, owner)
    expect(owner).not.toBe(current.owner)
    expect(current.results.forOwner(owner)).toMatchObject({ kind: 'mfa-enrollment', sharedKey: 'new-shared-key' })
    current.results.discard()
    expect(current.results.forOwner(owner)).toBeNull()
  })

  it('regeneração mantém o SID quando o servidor identifica exatamente essa sessão', () => {
    const current = fixture()
    const ticket = codes(current, SID)
    const owner = confirm(current, ticket, { sessionId: SID })
    current.results.accept(ticket, { owner, mfaEnabled: true })
    current.context.completeOwnerInstallation(current.context.getSnapshot().transitionEpoch, owner)
    expect(current.results.forOwner(owner)?.kind).toBe('recovery-codes')
  })

  it('falha na confirmação do recebimento dá aviso explícito e não mantém códigos', () => {
    const current = fixture()
    const ticket = codes(current)
    current.results.fail(ticket)
    expect(current.results.getSnapshot().phase).toBe('failed')
    expect(current.results.forOwner(current.owner)).toBeNull()
  })

  it('proteção reentrante durante a entrada não ressuscita resultado e mantém aviso de conclusão incerta', () => {
    const current = fixture()
    let first = true
    current.context.subscribe(() => {
      if (first) {
        first = false
        current.context.protect('identity')
      }
    })
    expect(() => codes(current)).toThrow(expect.objectContaining({ name: 'AbortError' }))
    expect(current.results.getSnapshot().phase).toBe('failed')
    expect(current.results.forOwner(current.owner)).toBeNull()
  })
  it('proteção reentrante na publicação inicial também resulta em AbortError, sem payload', () => {
    const current = fixture()
    current.results.subscribe(() => {
      if (current.results.getSnapshot().phase === 'pending') current.context.protect('identity')
    })
    expect(() => codes(current)).toThrow(expect.objectContaining({ name: 'AbortError' }))
    expect(current.results.getSnapshot().phase).toBe('failed')
    expect(current.results.forOwner(current.owner)).toBeNull()
  })
})
