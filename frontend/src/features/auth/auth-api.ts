import { z } from 'zod'
import { sendMutation, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'
import type { EmailLink } from './auth-schemas'

type LoginRequest = components['schemas']['LoginRequest']
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
}

/** 202 neutro: a resposta não revela se o e-mail já tem conta. */
export function register(registration: Registration): Promise<void> {
  return sendMutation({ method: 'POST', path: '/api/v1/auth/register', body: registration })
}

/**
 * Exige a senha do cadastro além do token: um link de confirmação gerado por outra pessoa e
 * entregue na caixa da vítima não confirma a conta de quem o gerou. Senha errada responde como
 * token inválido (400 INVALID_TOKEN).
 */
export function confirmEmail(link: EmailLink, password: string): Promise<void> {
  return sendMutation({
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

export function login(credentials: Credentials): Promise<void> {
  return sendMutation({
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
export function requestPasswordReset(email: string): Promise<void> {
  return sendMutation({ method: 'POST', path: '/api/v1/auth/password-reset-requests', body: { email } })
}

export function resetPassword(link: EmailLink, newPassword: string): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: '/api/v1/auth/password-resets',
    body: { userId: link.userId, token: link.token, newPassword },
  })
}

export function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  return sendMutation({
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
}) satisfies z.ZodType<RecoveryCodesResponse>

export type MfaEnrollment = z.output<typeof mfaEnrollmentSchema>

/** Inicia o cadastro do TOTP (API-A11). A chave volta uma vez; quem chama a guarda só em memória local. */
export async function startMfaEnrollment(): Promise<MfaEnrollment> {
  return mfaEnrollmentSchema.parse(await sendMutationForJson({ method: 'POST', path: '/api/v1/me/mfa/enrollments' }))
}

/** Confirma o TOTP (API-A12) e devolve os códigos de recuperação, mostrados uma vez. */
export async function confirmMfa(code: string): Promise<readonly string[]> {
  const body = await sendMutationForJson({
    method: 'POST',
    path: '/api/v1/me/mfa/confirmations',
    body: { code } satisfies ConfirmMfaRequest,
  })
  return recoveryCodesSchema.parse(body).recoveryCodes
}
