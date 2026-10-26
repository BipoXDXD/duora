import type { OperationHandle } from '@/shared/session-context'
import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const preferencesSchema = z.object({
  communicationLanguages: z.array(z.enum(['en', 'pt'])),
  allowDifferentDisplayLocales: z.boolean(),
  declarationRequired: z.boolean(),
  version: z.string().regex(/^\d+$/),
}) satisfies z.ZodType<components['schemas']['OwnPreferencesResponse']>
const localeSchema = z.object({
  locale: z.enum(['en-US', 'pt-BR']),
  version: z.string().regex(/^\d+$/),
}) satisfies z.ZodType<components['schemas']['OwnLocaleResponse']>
export type CommunicationPreferences = z.output<typeof preferencesSchema>
export type AccountLocale = z.output<typeof localeSchema>
export type Language = CommunicationPreferences['communicationLanguages'][number]
export async function fetchPreferences(
  operation: OperationHandle,
  signal?: AbortSignal,
): Promise<CommunicationPreferences> {
  return preferencesSchema.parse(
    await apiClient.requestJson({
      operation,
      method: 'GET',
      path: '/api/v1/me/preferences',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function savePreferences(
  operation: OperationHandle,
  base: string,
  changes: Pick<CommunicationPreferences, 'communicationLanguages' | 'allowDifferentDisplayLocales'>,
): Promise<CommunicationPreferences> {
  return preferencesSchema.parse(
    await sendMutationForJson({
      operation,
      method: 'PUT',
      path: '/api/v1/me/preferences',
      headers: { 'If-Match': `"${base}"` },
      body: changes,
    }),
  )
}
export async function fetchLocale(operation: OperationHandle, signal?: AbortSignal): Promise<AccountLocale> {
  return localeSchema.parse(
    await apiClient.requestJson({
      operation,
      method: 'GET',
      path: '/api/v1/me/locale',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function saveLocale(
  operation: OperationHandle,
  base: string,
  locale: AccountLocale['locale'],
): Promise<AccountLocale> {
  return localeSchema.parse(
    await sendMutationForJson({
      operation,
      method: 'PUT',
      path: '/api/v1/me/locale',
      headers: { 'If-Match': `"${base}"` },
      body: { locale },
    }),
  )
}
