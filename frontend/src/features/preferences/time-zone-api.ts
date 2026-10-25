import type { OperationHandle } from '@/shared/session-context'
import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const schema = z.object({ timeZone: z.string().nullable(), version: z.string().regex(/^\d+$/) }) satisfies z.ZodType<
  components['schemas']['OwnTimeZoneResponse']
>
const optionsSchema = z.object({ timeZones: z.array(z.string()) }) satisfies z.ZodType<
  components['schemas']['TimeZoneOptionsResponse']
>
export type AccountTimeZone = z.output<typeof schema>
export async function fetchTimeZone(operation: OperationHandle, signal?: AbortSignal): Promise<AccountTimeZone> {
  return schema.parse(
    await apiClient.requestJson({
      operation,
      method: 'GET',
      path: '/api/v1/me/time-zone',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function fetchTimeZones(operation: OperationHandle, signal?: AbortSignal): Promise<string[]> {
  return optionsSchema.parse(
    await apiClient.requestJson({
      operation,
      method: 'GET',
      path: '/api/v1/me/time-zone/options',
      ...(signal === undefined ? {} : { signal }),
    }),
  ).timeZones
}
export async function saveTimeZone(
  operation: OperationHandle,
  base: string,
  timeZone: string | null,
): Promise<AccountTimeZone> {
  return schema.parse(
    await sendMutationForJson({
      operation,
      method: 'PUT',
      path: '/api/v1/me/time-zone',
      headers: { 'If-Match': `"${base}"` },
      body: { timeZone },
    }),
  )
}
