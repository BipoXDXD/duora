import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'

const path = '/api/v1/me/selfie-verifications'
const statusSchema = z.object({
  id: z.uuid().nullable(),
  state: z.enum([
    'NotStarted',
    'Pending',
    'Capturing',
    'Processing',
    'Passed',
    'RecaptureRequired',
    'Inconclusive',
    'Expired',
  ]),
  nextStep: z.enum(['Start', 'None', 'Capture', 'Wait', 'Recapture']),
  feedbackCode: z.string().nullable(),
  expiresAt: z.string().nullable(),
})
const captureSchema = z.object({
  sessionId: z.uuid(),
  region: z.literal('us-east-1'),
  credentials: z.object({
    accessKeyId: z.string(),
    secretAccessKey: z.string(),
    sessionToken: z.string(),
    expiration: z.string(),
  }),
})
export type SelfieStatus = z.infer<typeof statusSchema>
export type SelfieCapture = z.infer<typeof captureSchema>
export async function currentSelfie(signal?: AbortSignal): Promise<SelfieStatus> {
  return statusSchema.parse(
    await apiClient.requestJson({ method: 'GET', path: `${path}/current`, ...(signal ? { signal } : {}) }),
  )
}
export async function startSelfie(): Promise<SelfieStatus> {
  return statusSchema.parse(await sendMutationForJson({ method: 'POST', path, body: {} }))
}
export async function captureSelfie(id: string): Promise<SelfieCapture> {
  return captureSchema.parse(await sendMutationForJson({ method: 'POST', path: `${path}/${id}/capture`, body: {} }))
}
export async function completeSelfie(id: string): Promise<SelfieStatus> {
  return statusSchema.parse(await sendMutationForJson({ method: 'POST', path: `${path}/${id}/complete`, body: {} }))
}
