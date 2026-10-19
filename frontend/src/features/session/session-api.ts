import { z } from 'zod'
import { apiClient, isUnauthorized, sendMutation } from '@/shared/api'
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
  preferredLocale: z.enum(['en-US', 'pt-BR']).nullable().optional(),
}) satisfies z.ZodType<Omit<MeResponse, 'preferredLocale'> & { preferredLocale?: string | null | undefined }>

export type Me = z.output<typeof meSchema>

const sessionSchema = z.object({
  id: z.string().min(1),
  createdAt: z.iso.datetime(),
  lastSeenAt: z.iso.datetime(),
  current: z.boolean(),
  description: z.string(),
})

const sessionListSchema = z.object({ items: z.array(sessionSchema) })

export type AccountSession = z.output<typeof sessionSchema>

export async function fetchMe(signal?: AbortSignal): Promise<Me> {
  const body = await apiClient.requestJson({
    method: 'GET',
    path: '/api/v1/me',
    ...(signal === undefined ? {} : { signal }),
  })
  return meSchema.parse(body)
}

export async function fetchSessions(signal?: AbortSignal): Promise<readonly AccountSession[]> {
  const body = await apiClient.requestJson({
    method: 'GET',
    path: '/api/v1/me/sessions',
    ...(signal === undefined ? {} : { signal }),
  })
  return sessionListSchema.parse(body).items
}

/** Encerra a sessão corrente. Se ela já tinha expirado (401), o objetivo foi cumprido. */
export async function logout(): Promise<void> {
  try {
    await sendMutation({ method: 'POST', path: '/api/v1/auth/logout' })
  } catch (error) {
    if (!isUnauthorized(error)) {
      throw error
    }
  }
}

export async function revokeSession(sessionId: string): Promise<void> {
  await sendMutation({ method: 'DELETE', path: `/api/v1/me/sessions/${encodeURIComponent(sessionId)}` })
}
