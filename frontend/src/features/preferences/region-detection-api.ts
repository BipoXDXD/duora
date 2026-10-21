import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const capabilitySchema = z.object({ enabled: z.boolean() }) satisfies z.ZodType<
  components['schemas']['RegionDetectionCapabilityResponse']
>
const resultSchema = z.discriminatedUnion('status', [
  z.object({
    status: z.literal('detected'),
    countryCode: z.literal('BR'),
    observedAt: z.iso.datetime({ offset: true }),
  }),
  z.object({
    status: z.enum(['unknown', 'unsupported', 'unavailable', 'disabled']),
    countryCode: z.null(),
    observedAt: z.iso.datetime({ offset: true }),
  }),
]) satisfies z.ZodType<components['schemas']['RegionDetectionResponse']>

export async function fetchRegionDetectionCapability(signal: AbortSignal): Promise<boolean> {
  return capabilitySchema.parse(
    await apiClient.requestJson({ method: 'GET', path: '/api/v1/me/region-detection/capability', signal }),
  ).enabled
}
export async function suggestRegion(signal: AbortSignal): Promise<z.output<typeof resultSchema>> {
  return resultSchema.parse(await sendMutationForJson({ method: 'POST', path: '/api/v1/me/region-detection', signal }))
}
