import { z } from 'zod'
import type { components } from '@/shared/api/schema'
import { REGION_CODES } from './profile-region'
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
  | 'profile.error.unknownRegion'

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

/** Valor do select: '' é "Não informar"; o resto, só as UFs do contrato, na forma canônica como o servidor exige. */
const regionSchema = z.union([z.literal(''), z.enum(REGION_CODES)], {
  error: () => 'profile.error.unknownRegion' satisfies ProfileErrorKey,
})

export const profileFormSchema = z
  .object({
    displayName: displayNameSchema,
    bio: bioSchema,
    countryCode: z.enum(['', 'BR', 'US'], { error: () => 'profile.error.unknownRegion' satisfies ProfileErrorKey }),
    subdivisionCode: regionSchema,
  })
  .superRefine((value, context) => {
    if (
      value.subdivisionCode !== '' &&
      (value.countryCode === '' || !value.subdivisionCode.startsWith(`${value.countryCode}-`))
    ) {
      context.addIssue({
        code: 'custom',
        path: ['subdivisionCode'],
        message: 'profile.error.unknownRegion' satisfies ProfileErrorKey,
      })
    }
  })

export type ProfileFormInput = z.input<typeof profileFormSchema>
export type ProfileFormValues = z.output<typeof profileFormSchema>

/** Campos que a pessoa mexeu, no formato do `dirtyFields` do react-hook-form. */
export type EditedFields = Readonly<Partial<Record<keyof ProfileFormValues, boolean>>>

/**
 * Corpo do PATCH só com o que a pessoa editou: depois de um 412 e de recarregar, reaplicar não desfaz o que a outra
 * aba mudou nos campos que ela não tocou. Descrição apagada e região "Não informar" viram `null`.
 */
export function toProfileChanges(values: ProfileFormValues, edited: EditedFields): UpdateProfileRequest {
  return {
    ...(edited.displayName === true ? { displayName: values.displayName } : {}),
    ...(edited.bio === true ? { bio: values.bio === '' ? null : values.bio } : {}),
    ...(edited.countryCode === true || edited.subdivisionCode === true
      ? {
          countryCode: values.countryCode === '' ? null : values.countryCode,
          subdivisionCode: values.subdivisionCode === '' ? null : values.subdivisionCode,
        }
      : {}),
  }
}
