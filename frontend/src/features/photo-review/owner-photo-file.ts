import type { paths } from '@/shared/api/schema'

export type PhotoContentType = keyof paths['/api/v1/me/photos']['post']['requestBody']['content']

export type PhotoFileRefusal = 'empty' | 'too-large' | 'unsupported-type' | 'unreadable'

/** Os bytes ficam congelados: repetir o envio com a mesma chave precisa mandar exatamente o mesmo corpo. */
export interface PhotoToUpload {
  readonly contentType: PhotoContentType
  readonly bytes: ArrayBuffer
}

export type PhotoFileReading =
  | { readonly kind: 'ready'; readonly photo: PhotoToUpload }
  | { readonly kind: 'refused'; readonly reason: PhotoFileRefusal }

/** Teto de `PhotoLimits.MaxBytes`; o tipo real da imagem é conferido só no servidor. */
const MAX_PHOTO_BYTES = 5 * 1024 * 1024

const CONTENT_TYPES: Readonly<Record<PhotoContentType, true>> = {
  'image/jpeg': true,
  'image/png': true,
  'image/webp': true,
}

export const PHOTO_FILE_ACCEPT = Object.keys(CONTENT_TYPES).join(',')

function isPhotoContentType(value: string): value is PhotoContentType {
  return Object.hasOwn(CONTENT_TYPES, value)
}

function sizeRefusal(size: number): PhotoFileRefusal | null {
  if (size === 0) return 'empty'
  return size > MAX_PHOTO_BYTES ? 'too-large' : null
}

/** Recusa local, antes de gastar cota ou rate limit do envio. */
export function checkPhotoFile(file: File): PhotoFileRefusal | null {
  return isPhotoContentType(file.type) ? sizeRefusal(file.size) : 'unsupported-type'
}

export async function readPhotoFile(file: File): Promise<PhotoFileReading> {
  const contentType = file.type
  if (!isPhotoContentType(contentType)) return { kind: 'refused', reason: 'unsupported-type' }
  const refusal = sizeRefusal(file.size)
  if (refusal !== null) return { kind: 'refused', reason: refusal }
  try {
    return { kind: 'ready', photo: { contentType, bytes: await file.arrayBuffer() } }
  } catch {
    // O arquivo saiu do disco ou perdeu a permissão de leitura depois de escolhido.
    return { kind: 'refused', reason: 'unreadable' }
  }
}
