import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const statusSchema = z.object({
  state: z.enum(['NotStarted', 'Pending', 'VerifiedAdult', 'Rejected', 'Expired', 'ManualReview']),
  nextStep: z.enum([
    'None',
    'StartVerification',
    'WaitForProvider',
    'ContinueWithProvider',
    'WaitForReview',
    'NotAvailable',
  ]),
  continueUrl: z.string().nullable(),
}) satisfies z.ZodType<components['schemas']['AgeVerificationResponse']>

export type AgeStatus = z.output<typeof statusSchema>

export async function fetchAgeStatus(signal?: AbortSignal): Promise<AgeStatus> {
  return statusSchema.parse(
    await apiClient.requestJson({
      method: 'GET',
      path: '/api/v1/me/age-verifications/current',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}

export async function startAgeVerification(): Promise<AgeStatus> {
  return statusSchema.parse(
    await sendMutationForJson({
      method: 'POST',
      path: '/api/v1/me/age-verifications',
      body: {},
    }),
  )
}

/** A allowlist é aplicada pelo servidor; a SPA também recusa esquemas executáveis e credenciais. */
export function safeFlowUrl(value: string | null): string | null {
  if (value === null) return null
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && url.username === '' && url.password === '' ? url.href : null
  } catch {
    return null
  }
}
