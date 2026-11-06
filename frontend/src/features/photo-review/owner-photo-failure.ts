import { ApiProblemError } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { reviewHttpStatus } from './review-command'

const UPLOADS_DISABLED = 'MEDIA_UPLOADS_DISABLED'
const REQUEST_IN_PROGRESS = 'IDEMPOTENCY_REQUEST_IN_PROGRESS'

/** Recusas em que o servidor garante que nada mudou: a pessoa pode decidir de novo, com chave nova. */
const DEFINITIVE_STATUSES: ReadonlySet<number> = new Set([400, 401, 403, 404, 409, 411, 412, 413, 415, 428, 429])

const PROBLEM_MESSAGES: Readonly<Record<string, PlainMessageKey>> = {
  ACCOUNT_RESTRICTED: 'photoOwner.accountCannotUpload',
  LENGTH_REQUIRED: 'photoOwner.uploadInterrupted',
  MEDIA_QUEUE_FULL: 'photoOwner.queueFull',
  PAYLOAD_TOO_LARGE: 'photoOwner.fileTooLarge',
  PHOTO_DELETE_STATE_CONFLICT: 'photoOwner.stillArriving',
  PRECONDITION_FAILED: 'photoOwner.photoChanged',
  UNSUPPORTED_MEDIA_TYPE: 'photoOwner.fileType',
  UPLOAD_INCOMPLETE: 'photoOwner.uploadInterrupted',
  UPLOAD_PENDING_LIMIT: 'photoOwner.pendingLimit',
}

function problemCode(error: unknown): string | null {
  return error instanceof ApiProblemError ? (error.problem.code ?? null) : null
}

/** Mensagem própria da recusa, ou null quando ela cabe no alerta comum. */
export function ownerPhotoProblemMessage(error: unknown): PlainMessageKey | null {
  const code = problemCode(error)
  return code !== null && Object.hasOwn(PROBLEM_MESSAGES, code) ? (PROBLEM_MESSAGES[code] ?? null) : null
}

/** O gate do upload no servidor: sem `Media:Review`, as rotas de foto respondem este código. */
export function isUploadsDisabled(error: unknown): boolean {
  return problemCode(error) === UPLOADS_DISABLED
}

/** Rede, 5xx e resposta fora do contrato podem ter produzido efeito; só a mesma chave pode repetir o pedido. */
export function isOwnerPhotoOutcomeUncertain(error: unknown): boolean {
  const code = problemCode(error)
  if (code === REQUEST_IN_PROGRESS) return true
  return code !== UPLOADS_DISABLED && !DEFINITIVE_STATUSES.has(reviewHttpStatus(error) ?? 0)
}

/** A foto saiu (404) ou o gate fechou: a lista em tela deixou de valer. */
export function invalidatesOwnerPhotoList(error: unknown): boolean {
  return reviewHttpStatus(error) === 404 || isUploadsDisabled(error)
}
