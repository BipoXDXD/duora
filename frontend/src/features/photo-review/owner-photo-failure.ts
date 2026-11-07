import { ApiProblemError } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { reviewHttpStatus } from './review-command'

const UPLOADS_DISABLED = 'MEDIA_UPLOADS_DISABLED'
const REQUEST_IN_PROGRESS = 'IDEMPOTENCY_REQUEST_IN_PROGRESS'
const KEY_REUSED = 'IDEMPOTENCY_KEY_REUSED'

/** Recusas em que o servidor garante que nada mudou: a pessoa pode decidir de novo, com chave nova. */
const DEFINITIVE_STATUSES: ReadonlySet<number> = new Set([400, 401, 403, 404, 409, 411, 412, 413, 415, 428, 429])

/**
 * Respostas que dizem algo sobre uma tentativa incerta anterior da mesma chave. Gate, autorização, rate limit e
 * validação de headers vêm antes de o servidor consultar a chave. `UPLOAD_INCOMPLETE` também não entra: a repetição
 * lê o corpo de novo e responde o mesmo 400 mesmo com a foto já gravada.
 */
const UPLOAD_CONCLUSIVE_CODES: ReadonlySet<string> = new Set([KEY_REUSED, 'UPLOAD_PENDING_LIMIT'])
const DELETION_CONCLUSIVE_CODES: ReadonlySet<string> = new Set([
  KEY_REUSED,
  'PHOTO_DELETE_STATE_CONFLICT',
  'PRECONDITION_FAILED',
])

const PROBLEM_MESSAGES: Readonly<Record<string, PlainMessageKey>> = {
  ACCOUNT_RESTRICTED: 'photoOwner.accountCannotUpload',
  [KEY_REUSED]: 'photoOwner.keyReused',
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
function isFirstAttemptUncertain(error: unknown): boolean {
  return !isUploadsDisabled(error) && !DEFINITIVE_STATUSES.has(reviewHttpStatus(error) ?? 0)
}

export function isUploadOutcomeUncertain(error: unknown, repeating: boolean): boolean {
  const code = problemCode(error)
  if (code === REQUEST_IN_PROGRESS) return true
  return repeating ? !UPLOAD_CONCLUSIVE_CODES.has(code ?? '') : isFirstAttemptUncertain(error)
}

/** No DELETE, o 404 vem depois da consulta do recibo: a exclusão confirmada teria respondido 204. */
export function isDeletionOutcomeUncertain(error: unknown, repeating: boolean): boolean {
  const code = problemCode(error)
  if (code === REQUEST_IN_PROGRESS) return true
  if (!repeating) return isFirstAttemptUncertain(error)
  return reviewHttpStatus(error) !== 404 && !DELETION_CONCLUSIVE_CODES.has(code ?? '')
}

/** A foto saiu (404), o gate fechou ou a chave já tem outro desfecho: a lista em tela deixou de valer. */
export function invalidatesOwnerPhotoList(error: unknown): boolean {
  return reviewHttpStatus(error) === 404 || isUploadsDisabled(error) || problemCode(error) === KEY_REUSED
}
