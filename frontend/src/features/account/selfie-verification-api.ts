import type { OperationHandle } from '@/shared/session-context'
import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'

const path = '/api/v1/me/selfie-verifications'

const KNOWN_ATTEMPT_STATES = ['Available', 'CoolingDown', 'Exhausted'] as const

/** Situação do limite de reprovações (issue #300); valor novo do servidor vira 'Unknown' (spec 8.1). */
export type SelfieAttemptState = (typeof KNOWN_ATTEMPT_STATES)[number] | 'Unknown'

function toAttemptState(value: string): SelfieAttemptState {
  return KNOWN_ATTEMPT_STATES.find((state) => state === value) ?? 'Unknown'
}

const attemptsSchema = z.object({
  state: z.string().transform(toAttemptState),
  remaining: z.number().int().min(0),
  lastCycle: z.boolean(),
  lockedUntil: z.iso.datetime().nullable(),
})

/** Códigos do limite (issue #300): a tela relê o estado em vez de mostrar o aviso genérico. */
export const SELFIE_ATTEMPT_PROBLEMS: ReadonlySet<string> = new Set([
  'SELFIE_ATTEMPTS_LOCKED',
  'SELFIE_ATTEMPTS_EXHAUSTED',
])

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
  // Tolerant reader: sem o campo, a tela não mostra contagem e o servidor continua aplicando o limite.
  attempts: attemptsSchema.optional(),
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
export type SelfieAttempts = z.infer<typeof attemptsSchema>
export type SelfieCapture = z.infer<typeof captureSchema>
export async function currentSelfie(operation: OperationHandle, signal?: AbortSignal): Promise<SelfieStatus> {
  return statusSchema.parse(
    await apiClient.requestJson({ operation, method: 'GET', path: `${path}/current`, ...(signal ? { signal } : {}) }),
  )
}
export async function startSelfie(operation: OperationHandle): Promise<SelfieStatus> {
  return statusSchema.parse(await sendMutationForJson({ operation, method: 'POST', path, body: {} }))
}
export async function captureSelfie(operation: OperationHandle, id: string): Promise<SelfieCapture> {
  return captureSchema.parse(
    await sendMutationForJson({ operation, method: 'POST', path: `${path}/${id}/capture`, body: {} }),
  )
}
export async function completeSelfie(operation: OperationHandle, id: string): Promise<SelfieStatus> {
  return statusSchema.parse(
    await sendMutationForJson({ operation, method: 'POST', path: `${path}/${id}/complete`, body: {} }),
  )
}
