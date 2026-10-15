import { z } from 'zod'
import { apiClient, sendMutationForJson } from '@/shared/api'
import type { components } from '@/shared/api/schema'

type OwnProfileResponse = components['schemas']['OwnProfileResponse']
type UpdateProfileRequest = components['schemas']['UpdateProfileRequest']

const PROFILE_PATH = '/api/v1/me/profile'

const ownProfileSchema = z.object({
  displayName: z.string().nullable(),
  bio: z.string().nullable(),
  version: z.string().regex(/^\d+$/),
}) satisfies z.ZodType<OwnProfileResponse>

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
