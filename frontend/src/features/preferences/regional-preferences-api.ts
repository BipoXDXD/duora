import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const schema = z
  .object({
    mode: z.enum(['pending', 'countries', 'unrestricted']),
    countryCodes: z.array(z.literal('BR')),
    version: z.string().regex(/^\d+$/),
  })
  .refine((value) =>
    value.mode === 'countries' ? value.countryCodes.length === 1 : value.countryCodes.length === 0,
  ) satisfies z.ZodType<components['schemas']['OwnRegionalPreferencesResponse']>

export type RegionalPreferences = z.output<typeof schema>
export async function fetchRegionalPreferences(signal?: AbortSignal): Promise<RegionalPreferences> {
  return schema.parse(
    await apiClient.requestJson({
      method: 'GET',
      path: '/api/v1/me/regional-preferences',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function saveRegionalPreferences(
  base: string,
  mode: 'countries' | 'unrestricted',
  countryCodes: 'BR'[],
): Promise<RegionalPreferences> {
  return schema.parse(
    await sendMutationForJson({
      method: 'PUT',
      path: '/api/v1/me/regional-preferences',
      headers: { 'If-Match': `"${base}"` },
      body: { mode, countryCodes },
    }),
  )
}
