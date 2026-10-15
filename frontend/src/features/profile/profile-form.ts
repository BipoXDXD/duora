import { z } from 'zod'
import type { components } from '@/shared/api/schema'

type UpdateProfileRequest = components['schemas']['UpdateProfileRequest']

/** Limites do contrato (UpdateProfileRequest), em caracteres Unicode, como o backend conta. */
export const DISPLAY_NAME_MAX_LENGTH = 40
export const BIO_MAX_LENGTH = 500

/** Chaves `profile.error.*`: o schema guarda a chave e a tela traduz no idioma ativo. */
export type ProfileErrorKey =
  | 'profile.error.displayNameRequired'
  | 'profile.error.displayNameTooLong'
  | 'profile.error.bioTooLong'
  | 'profile.error.invalidCharacters'

// Mesmas classes que o backend recusa (ProfileText.cs): controle (Cc), formatação (Cf: bidi, largura zero, soft
// hyphen), separador de linha e parágrafo e surrogate solto. Na descrição, quebra de linha e tabulação passam.
const FORBIDDEN_IN_NAME = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\p{Cs}]/u
const FORBIDDEN_IN_BIO = /[^\P{Cc}\n\r\t]|[\p{Cf}\p{Zl}\p{Zp}\p{Cs}]/u
const LONE_SURROGATE = /\p{Cs}/u

/** Conta code points, não grafemas: é a unidade do backend (runes) e do char_length do PostgreSQL. */
function codePointLength(value: string): number {
  return Array.from(value).length
}

/** NFC antes de aparar e medir, como o servidor; surrogate solto fica para a regra de caracteres recusá-lo. */
function normalize(value: string): string {
  return (LONE_SURROGATE.test(value) ? value : value.normalize('NFC')).trim()
}

const displayNameSchema = z
  .string()
  .transform(normalize)
  .pipe(
    z
      .string()
      .min(1, 'profile.error.displayNameRequired' satisfies ProfileErrorKey)
      .refine((value) => !FORBIDDEN_IN_NAME.test(value), 'profile.error.invalidCharacters' satisfies ProfileErrorKey)
      .refine(
        (value) => codePointLength(value) <= DISPLAY_NAME_MAX_LENGTH,
        'profile.error.displayNameTooLong' satisfies ProfileErrorKey,
      ),
  )

const bioSchema = z
  .string()
  .transform(normalize)
  .pipe(
    z
      .string()
      .refine((value) => !FORBIDDEN_IN_BIO.test(value), 'profile.error.invalidCharacters' satisfies ProfileErrorKey)
      .refine(
        (value) => codePointLength(value) <= BIO_MAX_LENGTH,
        'profile.error.bioTooLong' satisfies ProfileErrorKey,
      ),
  )

export const profileFormSchema = z.object({ displayName: displayNameSchema, bio: bioSchema })

export type ProfileFormInput = z.input<typeof profileFormSchema>
export type ProfileFormValues = z.output<typeof profileFormSchema>

/** Corpo do PATCH: descrição vazia vira `null`, que apaga a descrição no servidor. */
export function toProfileChanges(values: ProfileFormValues): UpdateProfileRequest {
  return { displayName: values.displayName, bio: values.bio === '' ? null : values.bio }
}
