import type { OperationHandle } from '@/shared/session-context'
import { z } from 'zod'
import { apiClient, ApiResponseError, sendMutation, sendMutationForJson } from '@/shared/api'

// Localized boundary for the M7 contract (2026-10-25). Backend owns final generated OpenAPI/types.
const instant = z.iso.datetime({ offset: true })
const opaque = z.string().min(1)
const id = z.uuid()
const reviewSchema = z.object({
  caseId: id,
  state: z.enum(['AwaitingReview', 'Approved', 'Rejected']),
  eTag: opaque,
  awaitingReviewAt: instant,
  reviewDeadline: instant,
  policyVersion: opaque,
})
const appealSchema = z.object({
  appealId: id,
  photoId: id,
  state: z.enum(['Open', 'ClosedApproved', 'ClosedRejected', 'ClosedUnavailable']),
  eTag: opaque,
  requestedAt: instant,
  responseTargetAt: instant,
  maximumRetentionUntil: instant,
  closedAt: instant.nullable(),
})
const capabilitiesSchema = z.object({
  assign: z.boolean(),
  evidence: z.boolean(),
  decide: z.boolean(),
  appeal: z.boolean(),
  recentMfaRequiredForDecision: z.boolean(),
})
const historySchema = z.object({
  photoId: id,
  photoETag: opaque,
  items: z.array(
    z.object({
      decisionId: id,
      decision: z.string(),
      reasonCode: z.string(),
      decidedAt: instant,
      isAppeal: z.boolean(),
    }),
  ),
  appeal: appealSchema.nullable(),
  canRequestAppeal: z.boolean(),
})
const decisionReceiptSchema = z.object({
  caseId: id,
  appealId: id.nullable(),
  decision: z.string(),
  eTag: opaque,
  photoETag: opaque,
  assetId: id.nullable(),
})
const appealReceiptSchema = z.object({
  appealId: id,
  photoId: id,
  photoETag: opaque,
  requestedAt: instant,
  responseTargetAt: instant,
  maximumRetentionUntil: instant,
})
const ownerPhotoPageSchema = z.object({
  items: z.array(
    z.object({ photoId: id, status: z.string(), rejectionCode: z.string().nullable(), createdAt: instant }),
  ),
  nextCursor: z.string().max(512).nullable(),
})

export type OwnerPhotoPage = z.output<typeof ownerPhotoPageSchema>

export type ReviewKind = 'review' | 'appeal'
export type ReviewCapabilities = z.output<typeof capabilitiesSchema>
export type PhotoReviewHistory = z.output<typeof historySchema>
export type PhotoAppeal = z.output<typeof appealSchema>
export type ReviewDecision = 'approve' | 'reject'
export type ReviewState = z.output<typeof reviewSchema>['state'] | PhotoAppeal['state']

export interface ReviewItem {
  readonly id: string
  readonly state: ReviewState
  readonly eTag: string
  readonly startedAt: string
  readonly deadline: string
  readonly retentionUntil?: string
  readonly policyVersion?: string
}

export interface ReviewPage {
  readonly items: readonly ReviewItem[]
  readonly nextCursor: string | null
}

export interface ReviewIntent {
  readonly kind: ReviewKind
  readonly id: string
  readonly eTag: string
  readonly key: string
  readonly command:
    | { readonly type: 'decision'; readonly decision: ReviewDecision }
    | { readonly type: 'assignment'; readonly reviewerId: string }
}

export const reviewPath = (kind: ReviewKind): string => `/admin/photo-${kind === 'review' ? 'reviews' : 'appeals'}`
const apiPath = (kind: ReviewKind): string => `/api/v1${reviewPath(kind)}`
const itemPath = (kind: ReviewKind, itemId: string): string => `${apiPath(kind)}/${encodeURIComponent(itemId)}`

async function get(operation: OperationHandle, path: string, signal?: AbortSignal): Promise<Response> {
  return apiClient.fetchFor(operation)(
    new Request(new URL(path, location.origin), {
      method: 'GET',
      cache: 'no-store',
      redirect: 'error',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}

async function json(response: Response): Promise<unknown> {
  if (response.headers.get('Content-Type')?.split(';')[0]?.trim().toLowerCase() !== 'application/json')
    throw new ApiResponseError(response.status, 'invalid review response')
  const body: unknown = await response.json()
  return body
}

function confirmETag(response: Response, eTag: string): void {
  if (response.headers.get('ETag') !== eTag) throw new ApiResponseError(response.status, 'missing or inconsistent ETag')
}

function toReview(value: z.output<typeof reviewSchema>): ReviewItem {
  return {
    id: value.caseId,
    state: value.state,
    eTag: value.eTag,
    startedAt: value.awaitingReviewAt,
    deadline: value.reviewDeadline,
    policyVersion: value.policyVersion,
  }
}

function toAppeal(value: PhotoAppeal): ReviewItem {
  return {
    id: value.appealId,
    state: value.state,
    eTag: value.eTag,
    startedAt: value.requestedAt,
    deadline: value.responseTargetAt,
    retentionUntil: value.maximumRetentionUntil,
  }
}

export async function fetchReviewCapabilities(
  operation: OperationHandle,
  signal?: AbortSignal,
): Promise<ReviewCapabilities> {
  return capabilitiesSchema.parse(await json(await get(operation, '/api/v1/admin/photo-reviews/capabilities', signal)))
}

/** Owner-only list; the list/items carry no ETag. History obtains the real photo ETag separately. */
export async function fetchOwnerPhotoPage(
  operation: OperationHandle,
  cursor: string | null,
  signal?: AbortSignal,
): Promise<OwnerPhotoPage> {
  const query = new URLSearchParams({ limit: '20' })
  if (cursor !== null) query.set('cursor', cursor)
  return ownerPhotoPageSchema.parse(await json(await get(operation, `/api/v1/me/photos?${query}`, signal)))
}

export async function fetchReviewPage(
  operation: OperationHandle,
  request: { readonly kind: ReviewKind; readonly cursor: string | null },
  signal?: AbortSignal,
): Promise<ReviewPage> {
  const { kind, cursor } = request
  const query = new URLSearchParams({ limit: '20' })
  if (cursor !== null) query.set('cursor', cursor)
  const body = await json(await get(operation, `${apiPath(kind)}?${query}`, signal))
  if (kind === 'review') {
    const page = z.object({ items: z.array(reviewSchema), nextCursor: z.string().nullable() }).parse(body)
    return { items: page.items.map(toReview), nextCursor: page.nextCursor }
  }
  const page = z.object({ items: z.array(appealSchema), nextCursor: z.string().nullable() }).parse(body)
  return { items: page.items.map(toAppeal), nextCursor: page.nextCursor }
}

export async function fetchReviewItem(
  operation: OperationHandle,
  request: { readonly kind: ReviewKind; readonly id: string },
  signal?: AbortSignal,
): Promise<ReviewItem> {
  const { kind, id: itemId } = request
  const response = await get(operation, itemPath(kind, itemId), signal)
  const body = await json(response)
  const item = kind === 'review' ? toReview(reviewSchema.parse(body)) : toAppeal(appealSchema.parse(body))
  confirmETag(response, item.eTag)
  if (item.id !== itemId) throw new ApiResponseError(response.status, 'inconsistent review resource')
  return item
}

export async function fetchReviewHistory(
  operation: OperationHandle,
  photoId: string,
  signal?: AbortSignal,
): Promise<PhotoReviewHistory> {
  const response = await get(operation, `/api/v1/me/photos/${encodeURIComponent(photoId)}/review-history`, signal)
  const history = historySchema.parse(await json(response))
  confirmETag(response, history.photoETag)
  if (history.photoId !== photoId || (history.appeal !== null && history.appeal.photoId !== photoId))
    throw new ApiResponseError(response.status, 'inconsistent photo history')
  return history
}

export async function requestPhotoAppeal(
  operation: OperationHandle,
  intent: { readonly photoId: string; readonly eTag: string; readonly key: string },
  signal?: AbortSignal,
): Promise<z.output<typeof appealReceiptSchema>> {
  const body = await sendMutationForJson({
    operation,
    method: 'POST',
    path: `/api/v1/me/photos/${encodeURIComponent(intent.photoId)}/appeals`,
    body: {},
    headers: { 'If-Match': intent.eTag, 'Idempotency-Key': intent.key },
    ...(signal === undefined ? {} : { signal }),
  })
  const receipt = appealReceiptSchema.parse(body)
  if (receipt.photoId !== intent.photoId) throw new ApiResponseError(201, 'inconsistent appeal receipt')
  return receipt
}

export async function sendReviewIntent(
  operation: OperationHandle,
  intent: ReviewIntent,
  signal?: AbortSignal,
): Promise<void> {
  const decision = intent.command.type === 'decision'
  const body = await sendMutationForJson({
    operation,
    method: 'POST',
    path: `${itemPath(intent.kind, intent.id)}/${decision ? 'decisions' : 'assignments'}`,
    body:
      intent.command.type === 'decision'
        ? {
            decision: intent.command.decision,
            reasonCode: intent.command.decision === 'approve' ? 'meets_policy' : 'does_not_meet_policy',
          }
        : { reviewerId: intent.command.reviewerId },
    headers: { 'If-Match': intent.eTag, 'Idempotency-Key': intent.key },
    ...(signal === undefined ? {} : { signal }),
  })
  if (decision) {
    const receipt = decisionReceiptSchema.parse(body)
    if (
      (intent.kind === 'review'
        ? receipt.caseId !== intent.id || receipt.appealId !== null
        : receipt.appealId !== intent.id) ||
      receipt.decision !== intent.command.decision
    )
      throw new ApiResponseError(200, 'inconsistent decision receipt')
  } else if (intent.kind === 'review') {
    if (z.object({ caseId: id, eTag: opaque }).parse(body).caseId !== intent.id)
      throw new ApiResponseError(200, 'inconsistent assignment receipt')
  } else if (z.object({ appealId: id, eTag: opaque }).parse(body).appealId !== intent.id)
    throw new ApiResponseError(200, 'inconsistent assignment receipt')
}

export async function fetchReviewEvidence(
  operation: OperationHandle,
  request: { readonly kind: ReviewKind; readonly id: string; readonly variant: 'card' | 'full' | 'thumb' },
  signal: AbortSignal,
): Promise<Blob> {
  const response = await get(operation, `${itemPath(request.kind, request.id)}/evidence/${request.variant}`, signal)
  if (response.headers.get('Content-Type')?.split(';')[0]?.trim().toLowerCase() !== 'image/webp')
    throw new ApiResponseError(response.status, 'invalid private image')
  return response.blob()
}

/** Reuses the existing session/CSRF/TOTP endpoint; never grants a local capability. */
export function reauthenticateReviewer(
  operation: OperationHandle,
  credentials: { readonly password: string; readonly twoFactorCode: string },
  signal?: AbortSignal,
): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/reauthenticate',
    body: credentials,
    ...(signal === undefined ? {} : { signal }),
  })
}
