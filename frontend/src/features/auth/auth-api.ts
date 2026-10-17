import { sendMutation } from '@/shared/api'
import type { EmailLink } from './auth-schemas'

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
  readonly twoFactorCode?: string | undefined
  readonly email: string
  readonly password: string
}

export function login(credentials: Credentials): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: '/api/v1/auth/login',
    body: {
      email: credentials.email,
      password: credentials.password,
      ...(credentials.twoFactorCode ? { twoFactorCode: credentials.twoFactorCode } : {}),
    },
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
  return sendMutation({ method: 'POST', path: '/api/v1/me/password-changes', body: { currentPassword, newPassword } })
}
