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

/** Versões dos documentos legais que a pessoa aceita no cadastro, enviadas ao servidor. */
export const LEGAL_DOCUMENT_VERSIONS = {
  terms: '2026-10-01',
  privacy: '2026-10-01',
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
