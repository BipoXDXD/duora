import { z } from 'zod'
import {
  apiClient,
  ApiProblemError,
  ApiResponseError,
  isUnauthorized,
  sendMutation,
  type ApiClient,
} from '@/shared/api'
import { parseProblemDetails } from '@/shared/api/problem-details'
import {
  type BootstrapSource,
  type OwnerHandle,
  type OperationHandle,
  type RevalidationTicket,
} from '@/shared/session-context'
import type { components } from '@/shared/api/schema'

type MeResponse = components['schemas']['MeResponse']

const KNOWN_ACCOUNT_STATUSES = [
  'PendingEmail',
  'PendingEligibility',
  'Active',
  'Restricted',
  'Suspended',
  'DeletionPending',
] as const

type KnownAccountStatus = (typeof KNOWN_ACCOUNT_STATUSES)[number]

/** Situação da conta; valor novo do servidor vira 'Unknown' em vez de quebrar a tela (spec 8.1). */
export type AccountStatus = KnownAccountStatus | 'Unknown'

function toAccountStatus(value: string): AccountStatus {
  return KNOWN_ACCOUNT_STATUSES.find((status) => status === value) ?? 'Unknown'
}

const meSchema = z.object({
  id: z.uuid(),
  email: z.string().min(1),
  status: z.string().transform(toAccountStatus),
  emailConfirmed: z.boolean(),
  // Obrigatórios: sem eles a guarda de MFA não decide, e a leitura falha em vez de liberar a área.
  mfaEnabled: z.boolean(),
  roles: z.array(z.string()),
  // Obrigatório: a ausência é erro de contrato, não "aceite em dia" (o aviso de novo aceite sumiria em silêncio).
  currentTermsAccepted: z.boolean(),
  preferredTimeZone: z.string().nullable().optional(),
  preferredLocale: z.enum(['en-US', 'pt-BR']).nullable().optional(),
}) satisfies z.ZodType<
  Omit<MeResponse, 'preferredLocale' | 'preferredTimeZone' | 'sessionContext'> & {
    preferredLocale?: string | null | undefined
    preferredTimeZone?: string | null | undefined
  }
>

export type Me = z.output<typeof meSchema> & { readonly owner: OwnerHandle }

const contextSchema = z.object({
  userId: z.uuid(),
  sessionId: z.uuid(),
  phase: z.enum(['active', 'mfa-enrollment', 'password-change']),
  requestToken: z.string().min(1),
})
const bootstrapSchema = meSchema.extend({ status: z.string(), sessionContext: contextSchema })
const restrictionSchema = z.object({
  status: z.literal(403),
  code: z.literal('PASSWORD_CHANGE_REQUIRED'),
  sessionContext: contextSchema.extend({ phase: z.literal('password-change') }),
})

/** Somente o bootstrap restrito de senha inicial carrega uma capacidade, sem corpo/prova no erro. */
export class SessionRestrictionError extends Error {
  readonly status = 403
  readonly code = 'PASSWORD_CHANGE_REQUIRED'
  readonly owner: OwnerHandle
  constructor(owner: OwnerHandle) {
    super('É necessário atualizar a senha para continuar.')
    this.name = 'SessionRestrictionError'
    this.owner = owner
  }
}

const sessionSchema = z.object({
  id: z.string().min(1),
  createdAt: z.iso.datetime(),
  lastSeenAt: z.iso.datetime(),
  current: z.boolean(),
  description: z.string(),
})

const sessionListSchema = z.object({ items: z.array(sessionSchema) })

export type AccountSession = z.output<typeof sessionSchema>

export async function fetchMe(
  signal?: AbortSignal,
  source: BootstrapSource = { kind: 'automatic' },
  client: ApiClient = apiClient,
): Promise<Me> {
  const store = client.sessionContext
  const ticket = store.beginBootstrap(source)
  try {
    const { status, body } = await client.readMeBootstrap(ticket, signal)
    if (status === 403) {
      const restricted = restrictionSchema.safeParse(body)
      if (!restricted.success) {
        const problem = parseProblemDetails(body, status)
        throw problem === null
          ? new ApiResponseError(status, 'restrição de sessão inválida')
          : new ApiProblemError(problem)
      }
      const owner = store.acceptBootstrap(ticket, { ...restricted.data.sessionContext, authView: null })
      throw new SessionRestrictionError(owner)
    }
    const parsed = bootstrapSchema.safeParse(body)
    if (!parsed.success) throw new ApiResponseError(status, 'contexto de sessão inválido')
    const { sessionContext, ...account } = parsed.data
    const expectedPhase =
      !account.mfaEnabled && account.roles.some((role) => role !== 'participant') ? 'mfa-enrollment' : 'active'
    if (account.id.toLowerCase() !== sessionContext.userId.toLowerCase() || sessionContext.phase !== expectedPhase) {
      throw new ApiResponseError(status, 'contexto de sessão incoerente')
    }
    const owner = store.acceptBootstrap(ticket, {
      ...sessionContext,
      authView: {
        status: account.status,
        emailConfirmed: account.emailConfirmed,
        mfaEnabled: account.mfaEnabled,
        roles: account.roles,
      },
    })
    return { ...account, status: toAccountStatus(account.status), owner }
  } catch (error) {
    if (error instanceof SessionRestrictionError || (error instanceof DOMException && error.name === 'AbortError'))
      throw error
    // O await que entregou o erro pode ter sido ultrapassado por um bootstrap mais novo.
    // Erro obsoleto também é aborto, sem proteger/limpar o dono que já foi confirmado.
    store.assertBootstrap(ticket)
    if (isUnauthorized(error)) store.acceptAnonymous(ticket)
    else store.protect('context')
    throw error
  } finally {
    store.finishBootstrap(ticket)
  }
}

export function fetchMeForRevalidation(ticket: RevalidationTicket, signal?: AbortSignal): Promise<Me> {
  return fetchMe(signal, { kind: 'explicit', ticket })
}

export async function fetchSessions(
  operation: OperationHandle,
  signal?: AbortSignal,
): Promise<readonly AccountSession[]> {
  const body = await apiClient.requestJson({
    method: 'GET',
    path: '/api/v1/me/sessions',
    operation,
    ...(signal === undefined ? {} : { signal }),
  })
  return sessionListSchema.parse(body).items
}

/** Encerra a sessão corrente. Se ela já tinha expirado (401), o objetivo foi cumprido. */
export async function logout(operation?: OperationHandle): Promise<void> {
  try {
    await sendMutation({
      method: 'POST',
      path: '/api/v1/auth/logout',
      ...(operation === undefined ? {} : { operation }),
    })
  } catch (error) {
    if (!isUnauthorized(error)) {
      throw error
    }
  }
}

export async function revokeSession(operation: OperationHandle, sessionId: string): Promise<void> {
  await sendMutation({ operation, method: 'DELETE', path: `/api/v1/me/sessions/${encodeURIComponent(sessionId)}` })
}
