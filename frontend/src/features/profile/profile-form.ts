import { z } from 'zod'
import type { components } from '@/shared/api/schema'
import { findTextProblem, normalizeProfileText, type TextProblem } from './profile-text'

type UpdateProfileRequest = components['schemas']['UpdateProfileRequest']

/** Limites do contrato (UpdateProfileRequest), em code points depois da NFC, como o backend conta. */
export const DISPLAY_NAME_MAX_LENGTH = 40
export const BIO_MAX_LENGTH = 500

/** Chaves `profile.error.*`: o schema guarda a chave e a tela traduz no idioma ativo. */
export type ProfileErrorKey =
  | 'profile.error.displayNameRequired'
  | 'profile.error.displayNameTooLong'
  | 'profile.error.bioTooLong'
  | 'profile.error.invalidCharacters'

const DISPLAY_NAME_ERRORS: Readonly<Record<TextProblem, ProfileErrorKey>> = {
  empty: 'profile.error.displayNameRequired',
  tooLong: 'profile.error.displayNameTooLong',
  forbiddenCharacter: 'profile.error.invalidCharacters',
}

const displayNameSchema = z
  .string()
  .transform(normalizeProfileText)
  .superRefine((value, context) => {
    const problem = findTextProblem(value, { maxLength: DISPLAY_NAME_MAX_LENGTH, allowLineBreaks: false })
    if (problem !== null) {
      context.addIssue({ code: 'custom', message: DISPLAY_NAME_ERRORS[problem] })
    }
  })

/** Descrição é opcional: vazia, só brancos ou só fillers vira '' (e o envio, null). */
const bioSchema = z
  .string()
  .transform(normalizeProfileText)
  .superRefine((value, context) => {
    const problem = findTextProblem(value, { maxLength: BIO_MAX_LENGTH, allowLineBreaks: true })
    if (problem === 'forbiddenCharacter') {
      context.addIssue({ code: 'custom', message: 'profile.error.invalidCharacters' satisfies ProfileErrorKey })
    } else if (problem === 'tooLong') {
      context.addIssue({ code: 'custom', message: 'profile.error.bioTooLong' satisfies ProfileErrorKey })
    }
  })
  .transform((value) =>
    findTextProblem(value, { maxLength: BIO_MAX_LENGTH, allowLineBreaks: true }) === 'empty' ? '' : value,
  )

export const profileFormSchema = z.object({ displayName: displayNameSchema, bio: bioSchema })

export type ProfileFormInput = z.input<typeof profileFormSchema>
export type ProfileFormValues = z.output<typeof profileFormSchema>

/** Campos que a pessoa mexeu, no formato do `dirtyFields` do react-hook-form. */
export type EditedFields = Readonly<Partial<Record<keyof ProfileFormValues, boolean>>>

/**
 * Corpo do PATCH só com o que a pessoa editou: depois de um 412 e de recarregar, reaplicar não desfaz o que a outra
 * aba mudou nos campos que ela não tocou. Descrição apagada vira `null`.
 */
export function toProfileChanges(values: ProfileFormValues, edited: EditedFields): UpdateProfileRequest {
  return {
    ...(edited.displayName === true ? { displayName: values.displayName } : {}),
    ...(edited.bio === true ? { bio: values.bio === '' ? null : values.bio } : {}),
  }
}
