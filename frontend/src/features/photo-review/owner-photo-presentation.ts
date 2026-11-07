import type { AccountStatus } from '@/features/session/session-api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'

const PHOTO_STATES: Readonly<Record<string, PlainMessageKey>> = {
  Pending: 'photoReview.photoPending',
  Quarantined: 'photoReview.photoQuarantined',
  Processing: 'photoReview.photoProcessing',
  AwaitingReview: 'photoReview.awaiting',
  Approved: 'photoReview.approved',
  Rejected: 'photoReview.rejected',
  Deleted: 'photoReview.photoDeleted',
}

const REJECTION_REASONS: Readonly<Record<string, PlainMessageKey>> = {
  PHOTO_REVIEW_REJECTED: 'photoReview.photoRejected',
  PHOTO_REVIEW_EXPIRED: 'photoReview.photoExpired',
  PHOTO_REVIEW_UNAVAILABLE: 'photoReview.photoUnavailable',
  UNSUPPORTED_FORMAT: 'photoOwner.reasonUnsupportedFormat',
  TYPE_MISMATCH: 'photoOwner.reasonTypeMismatch',
  TOO_MANY_PIXELS: 'photoOwner.reasonTooManyPixels',
  ANIMATED: 'photoOwner.reasonAnimated',
  CORRUPT_IMAGE: 'photoOwner.reasonCorruptImage',
  PROCESSING_LIMIT: 'photoOwner.reasonProcessingLimit',
}

const IN_REVIEW: ReadonlySet<string> = new Set(['Pending', 'Quarantined', 'Processing', 'AwaitingReview'])

/** Texto vindo do servidor nunca chega à tela: desconhecido vira apresentação local segura. */
export function photoStateMessage(status: string): PlainMessageKey {
  return Object.hasOwn(PHOTO_STATES, status)
    ? (PHOTO_STATES[status] ?? 'photoReview.unknownPhotoState')
    : 'photoReview.unknownPhotoState'
}

export function rejectionMessage(code: string | null): PlainMessageKey {
  return code !== null && Object.hasOwn(REJECTION_REASONS, code)
    ? (REJECTION_REASONS[code] ?? 'photoOwner.rejectedGeneric')
    : 'photoOwner.rejectedGeneric'
}

/** Só a recusa humana manda consultar as regras (`media-review.md`). */
export function citesPhotoRules(code: string | null): boolean {
  return code === 'PHOTO_REVIEW_REJECTED'
}

export function isInReview(status: string): boolean {
  return IN_REVIEW.has(status)
}

/** Espelha `PhotoOwnerAccess`: as demais contas recebem 403 no DELETE. */
const DELETING_STATUSES: ReadonlySet<AccountStatus> = new Set([
  'Active',
  'PendingEligibility',
  'Restricted',
  'Suspended',
])

export function canAccountDeletePhotos(status: AccountStatus): boolean {
  return DELETING_STATUSES.has(status)
}

/** `Pending` é o original ainda chegando: o servidor recusa a exclusão com 409. */
export function canDeletePhoto(status: string): boolean {
  return status !== 'Pending' && status !== 'Deleted'
}
