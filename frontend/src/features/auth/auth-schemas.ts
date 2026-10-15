import { z } from 'zod'
import { messages } from '@/shared/messages'

/**
 * Política de senha do contrato do M1: só comprimento, sem regras de composição (NIST 800-63B).
 * O comprimento conta unidades UTF-16, como o `string.Length` do ASP.NET Core Identity.
 */
export const PASSWORD_MIN_LENGTH = 12
export const PASSWORD_MAX_LENGTH = 128
// RFC 5321: o caminho de envio aceita no máximo 254 caracteres num endereço.
const EMAIL_MAX_LENGTH = 254

/** Versões dos documentos legais que a pessoa aceita no cadastro, enviadas ao servidor. */
export const LEGAL_DOCUMENT_VERSIONS = {
  terms: '2026-10-01',
  privacy: '2026-10-01',
} as const

// Validação de formato só para orientar; o servidor decide (spec 2.2, React Hook Form + Zod).
export const emailSchema = z
  .string()
  .trim()
  .min(1, messages.validation.emailRequired)
  .max(EMAIL_MAX_LENGTH, messages.validation.emailTooLong)
  .pipe(z.email(messages.validation.emailInvalid))

/** Senha nova (cadastro e redefinição): nunca é aparada, espaços contam. */
export const newPasswordSchema = z
  .string()
  .min(1, messages.validation.passwordRequired)
  .min(PASSWORD_MIN_LENGTH, messages.validation.passwordTooShort(PASSWORD_MIN_LENGTH))
  .max(PASSWORD_MAX_LENGTH, messages.validation.passwordTooLong(PASSWORD_MAX_LENGTH))

/** Senha atual na entrada: só exige preenchimento, para não travar contas de políticas antigas. */
export const currentPasswordSchema = z.string().min(1, messages.validation.passwordRequired)

/**
 * Parâmetros dos links de e-mail (`?userId=…&token=…`). A query string é dado externo: só vale
 * com userId UUID e token não vazio de tamanho limitado.
 */
const emailLinkSchema = z.object({
  userId: z.uuid(),
  token: z.string().min(1).max(2048),
})

export type EmailLink = z.output<typeof emailLinkSchema>

export function readEmailLink(searchParams: URLSearchParams): EmailLink | null {
  const parsed = emailLinkSchema.safeParse({
    userId: searchParams.get('userId'),
    token: searchParams.get('token'),
  })
  return parsed.success ? parsed.data : null
}
