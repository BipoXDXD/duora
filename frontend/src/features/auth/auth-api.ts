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

export function confirmEmail(link: EmailLink): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: '/api/v1/auth/confirm-email',
    body: { userId: link.userId, token: link.token },
  })
}

export interface Credentials {
  readonly email: string
  readonly password: string
}

export function login(credentials: Credentials): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: '/api/v1/auth/login',
    body: { email: credentials.email, password: credentials.password },
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
