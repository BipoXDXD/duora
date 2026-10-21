import { z } from 'zod'
import type { ValidationKey } from './validation-message'

/**
 * Política de senha do contrato do M1: só comprimento, sem regras de composição (NIST 800-63B).
 * O comprimento conta unidades UTF-16, como o `string.Length` do ASP.NET Core Identity.
 */
export const PASSWORD_MIN_LENGTH = 12
export const PASSWORD_MAX_LENGTH = 128
// RFC 5321: o caminho de envio aceita no máximo 254 caracteres num endereço.
export const EMAIL_MAX_LENGTH = 254

/**
 * Document metadata must come from the document actually presented, never from the UI/account locale.
 * The current registration only has version labels; document publication is pending issue #9.
 * Keep unknown languages null until published document metadata can be bound to each acceptance.
 */
export const REGISTRATION_DOCUMENTS = {
  terms: { version: '2026-10-01', locale: null },
  privacy: { version: '2026-10-01', locale: null },
} as const

export const LEGAL_DOCUMENT_VERSIONS = {
  terms: REGISTRATION_DOCUMENTS.terms.version,
  privacy: REGISTRATION_DOCUMENTS.privacy.version,
} as const

// Validação de formato só para orientar; o servidor decide (spec 2.2, React Hook Form + Zod).
// A mensagem de cada regra é a chave do catálogo; a tela a traduz (validation-message.ts).
export const emailSchema = z
  .string()
  .trim()
  .min(1, 'validation.emailRequired' satisfies ValidationKey)
  .max(EMAIL_MAX_LENGTH, 'validation.emailTooLong' satisfies ValidationKey)
  .pipe(z.email('validation.emailInvalid' satisfies ValidationKey))

/** Senha nova (cadastro e redefinição): nunca é aparada, espaços contam. */
export const newPasswordSchema = z
  .string()
  .min(1, 'validation.passwordRequired' satisfies ValidationKey)
  .min(PASSWORD_MIN_LENGTH, 'validation.passwordTooShort' satisfies ValidationKey)
  .max(PASSWORD_MAX_LENGTH, 'validation.passwordTooLong' satisfies ValidationKey)

/** Senha atual na entrada: só exige preenchimento, para não travar contas de políticas antigas. */
export const currentPasswordSchema = z.string().min(1, 'validation.passwordRequired' satisfies ValidationKey)

/**
 * Código TOTP de seis dígitos (RFC 6238, como o Identity gera); o servidor confere o valor. Espaços são ignorados,
 * como no servidor (auth.md, login com MFA): "123 456", como alguns autenticadores mostram, vira "123456".
 */
export const totpCodeSchema = z
  .string()
  .transform((code) => code.replace(/\s/g, ''))
  .pipe(z.string().regex(/^\d{6}$/, 'validation.totpCodeInvalid' satisfies ValidationKey))

/** Teto do servidor para o código de recuperação (RequestValidation.RecoveryCode). */
export const RECOVERY_CODE_MAX_LENGTH = 32

/**
 * Código de recuperação, alternativa ao TOTP no login (API-A04). O servidor confere o valor; aqui só se exige
 * preenchimento e o teto, sem os espaços das pontas que uma cópia costuma trazer.
 */
export const recoveryCodeSchema = z
  .string()
  .trim()
  .min(1, 'validation.recoveryCodeInvalid' satisfies ValidationKey)
  .max(RECOVERY_CODE_MAX_LENGTH, 'validation.recoveryCodeInvalid' satisfies ValidationKey)

/**
 * Parâmetros dos links de e-mail, no fragmento (`#userId=…&token=…`): o fragmento não vai ao
 * servidor, aos logs de acesso nem ao Referer (TM-19). É dado externo: só vale com userId UUID e
 * token não vazio de tamanho limitado.
 */
const emailLinkSchema = z.object({
  userId: z.uuid(),
  token: z.string().min(1).max(2048),
})

export type EmailLink = z.output<typeof emailLinkSchema>

export function readEmailLinkFragment(hash: string): EmailLink | null {
  const params = new URLSearchParams(hash.replace(/^#/, ''))
  const parsed = emailLinkSchema.safeParse({ userId: params.get('userId'), token: params.get('token') })
  return parsed.success ? parsed.data : null
}
