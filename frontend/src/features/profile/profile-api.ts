import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'
import { isCountryCode, isRegionCode } from './profile-region'

type OwnProfileResponse = components['schemas']['OwnProfileResponse']
type UpdateProfileRequest = components['schemas']['UpdateProfileRequest']

const PROFILE_PATH = '/api/v1/me/profile'

const ownProfileSchema = z
  .object({
    displayName: z.string().nullable(),
    bio: z.string().nullable(),
    /**
     * Leitura tolerante: um código que esta versão do frontend não conhece (o servidor ganhou uma UF antes do deploy
     * daqui) é exibido como "não informada" em vez de derrubar a tela. A escrita continua só com as 27 do select.
     */
    regionCode: z
      .string()
      .nullable()
      .transform((code) =>
        code !== null && code.startsWith('BR-') && isRegionCode(code)
          ? (code as OwnProfileResponse['regionCode'])
          : null,
      ),
    countryCode: z.string().nullable().optional(),
    subdivisionCode: z.string().nullable().optional(),
    version: z.string().regex(/^\d+$/),
  })
  .transform((profile) => ({
    ...profile,
    // Only absent fields use the legacy fallback. Explicit null must never resurrect a UF.
    countryCode:
      profile.countryCode === undefined
        ? profile.regionCode === null
          ? null
          : ('BR' as const)
        : profile.countryCode !== null && isCountryCode(profile.countryCode)
          ? profile.countryCode
          : null,
    subdivisionCode:
      profile.subdivisionCode === undefined
        ? profile.regionCode
        : profile.subdivisionCode !== null && isRegionCode(profile.subdivisionCode)
          ? profile.subdivisionCode
          : null,
  })) satisfies z.ZodType<OwnProfileResponse>

/** Perfil do titular (API-P01). `version` é o ETag sem aspas: o que a edição devolve no If-Match. */
export type OwnProfile = z.output<typeof ownProfileSchema>

export async function fetchOwnProfile(signal?: AbortSignal): Promise<OwnProfile> {
  const body = await apiClient.requestJson({
    method: 'GET',
    path: PROFILE_PATH,
    ...(signal === undefined ? {} : { signal }),
  })
  return ownProfileSchema.parse(body)
}

/**
 * Edição condicional (API-P02): só grava se o perfil ainda estiver na versão lida. Se outra aba gravou antes, o
 * servidor responde 412 e nada muda; quem chama decide como reconciliar, nunca repete às cegas.
 */
export async function updateOwnProfile(baseVersion: string, changes: UpdateProfileRequest): Promise<OwnProfile> {
  const body = await sendMutationForJson({
    method: 'PATCH',
    path: PROFILE_PATH,
    body: changes,
    headers: { 'If-Match': `"${baseVersion}"` },
  })
  return ownProfileSchema.parse(body)
}
