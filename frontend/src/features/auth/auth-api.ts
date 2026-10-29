import type { OperationHandle } from '@/shared/session-context'
import { z } from 'zod'
import { ApiResponseError, sendMutation, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'
import type { Locale } from '@/shared/i18n/locale'
import type { EmailLink } from './auth-schemas'

type LoginRequest = components['schemas']['LoginRequest']
type CreateEmailConfirmationRequest = components['schemas']['CreateEmailConfirmationRequest']
type ChangePasswordRequest = components['schemas']['ChangePasswordRequest']
type ConfirmMfaRequest = components['schemas']['ConfirmMfaRequest']
type MfaEnrollmentResponse = components['schemas']['MfaEnrollmentResponse']
type RecoveryCodesResponse = components['schemas']['RecoveryCodesResponse']

// Chamadas de autenticação do M1. Todas são mutações com token antiforgery renovado antes do envio.

export interface Registration {
  readonly email: string
  readonly password: string
  readonly acceptedTermsVersion: string
  readonly acceptedPrivacyVersion: string
  /** Exact published document language; it is independent of the account/interface preference. */
  readonly acceptedTermsLocale: Locale
  readonly acceptedTermsSha256: string
  readonly acceptedPrivacySha256: string
  readonly acceptedPrivacyLocale: Locale
  /** Explicit "18 or older" declaration required by the Terms; never an age or a birth date. */
  readonly confirmedAdult: true
}

/** 202 neutro: a resposta não revela se o e-mail já tem conta. */
export function register(operation: OperationHandle, registration: Registration): Promise<void> {
  return sendMutation({ operation, method: 'POST', path: '/api/v1/auth/register', body: registration })
}

/** 202 neutro; não substitui senha/aceites nem prolonga o cadastro pendente. */
export function requestEmailConfirmation(operation: OperationHandle, email: string, password: string): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/email-confirmation-requests',
    body: { email, password } satisfies CreateEmailConfirmationRequest,
  })
}

/**
 * Exige a senha do cadastro além do token: um link de confirmação gerado por outra pessoa e
 * entregue na caixa da vítima não confirma a conta de quem o gerou. Senha errada responde como
 * token inválido (400 INVALID_TOKEN).
 */
export function confirmEmail(operation: OperationHandle, link: EmailLink, password: string): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/confirm-email',
    body: { userId: link.userId, token: link.token, password },
  })
}

export interface Credentials {
  readonly email: string
  readonly password: string
  /** No máximo um dos dois segundos fatores; o servidor recusa os dois juntos. */
  readonly twoFactorCode?: string | undefined
  readonly recoveryCode?: string | undefined
}

export function login(operation: OperationHandle, credentials: Credentials): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/login',
    body: {
      email: credentials.email,
      password: credentials.password,
      ...(credentials.twoFactorCode ? { twoFactorCode: credentials.twoFactorCode } : {}),
      ...(credentials.recoveryCode ? { recoveryCode: credentials.recoveryCode } : {}),
    } satisfies LoginRequest,
  })
}

/** 202 neutro: a resposta não revela se o e-mail tem conta. */
export function requestPasswordReset(operation: OperationHandle, email: string): Promise<void> {
  return sendMutation({ operation, method: 'POST', path: '/api/v1/auth/password-reset-requests', body: { email } })
}

export function resetPassword(operation: OperationHandle, link: EmailLink, newPassword: string): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/password-resets',
    body: { userId: link.userId, token: link.token, newPassword },
  })
}

export function changePassword(
  operation: OperationHandle,
  currentPassword: string,
  newPassword: string,
): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/me/password-changes',
    body: { currentPassword, newPassword } satisfies ChangePasswordRequest,
  })
}

const mfaEnrollmentSchema = z.object({
  sharedKey: z.string().min(1),
  authenticatorUri: z.string().min(1),
}) satisfies z.ZodType<MfaEnrollmentResponse>

const recoveryCodesSchema = z.object({
  recoveryCodes: z.array(z.string().min(1)).min(1),
  resultSessionId: z.uuid(),
}) satisfies z.ZodType<RecoveryCodesResponse>

export type MfaEnrollment = z.output<typeof mfaEnrollmentSchema>

/** Inicia o cadastro do TOTP (API-A11). A chave volta uma vez; quem chama a guarda só em memória local. */
export async function startMfaEnrollment(operation: OperationHandle): Promise<MfaEnrollment> {
  const result = mfaEnrollmentSchema.safeParse(
    await sendMutationForJson({ operation, method: 'POST', path: '/api/v1/me/mfa/enrollments' }),
  )
  if (!result.success) throw new ApiResponseError(200, 'resultado de segurança inválido')
  return result.data
}

/** Confirma o TOTP (API-A12) e devolve os códigos de recuperação, mostrados uma vez. */
export async function confirmMfa(
  operation: OperationHandle,
  code: string,
): Promise<z.output<typeof recoveryCodesSchema>> {
  const body = await sendMutationForJson({
    operation,
    method: 'POST',
    path: '/api/v1/me/mfa/confirmations',
    body: { code } satisfies ConfirmMfaRequest,
  })
  const result = recoveryCodesSchema.safeParse(body)
  if (!result.success) throw new ApiResponseError(200, 'resultado de segurança inválido')
  return result.data
}
