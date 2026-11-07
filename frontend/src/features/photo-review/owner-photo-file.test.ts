import { describe, expect, it } from 'vitest'
import { checkPhotoFile, readPhotoFile } from './owner-photo-file'

const FIVE_MIB = 5 * 1024 * 1024

function fileOf(size: number, type: string): File {
  return new File([new Uint8Array(size)], 'synthetic', { type })
}

describe('arquivo escolhido para envio', () => {
  it.each(['image/jpeg', 'image/png', 'image/webp'])('aceita %s', (type) => {
    expect(checkPhotoFile(fileOf(1, type))).toBeNull()
  })

  it.each(['image/gif', 'image/heic', 'image/svg+xml', 'application/pdf', ''])('recusa o tipo "%s"', (type) => {
    expect(checkPhotoFile(fileOf(1, type))).toBe('unsupported-type')
  })

  it('recusa arquivo vazio', () => {
    expect(checkPhotoFile(fileOf(0, 'image/png'))).toBe('empty')
  })

  it('aceita exatamente 5 MiB e recusa um byte a mais', () => {
    expect(checkPhotoFile(fileOf(FIVE_MIB, 'image/png'))).toBeNull()
    expect(checkPhotoFile(fileOf(FIVE_MIB + 1, 'image/png'))).toBe('too-large')
  })

  it('congela os bytes lidos para que repetir o envio mande exatamente o mesmo corpo', async () => {
    const result = await readPhotoFile(new File([new Uint8Array([1, 2, 3])], 'synthetic', { type: 'image/webp' }))

    expect(result.kind).toBe('ready')
    if (result.kind !== 'ready') return
    expect(result.photo.contentType).toBe('image/webp')
    expect([...new Uint8Array(result.photo.bytes)]).toEqual([1, 2, 3])
  })

  it('devolve a recusa local sem ler o arquivo inválido', async () => {
    expect(await readPhotoFile(fileOf(0, 'image/png'))).toEqual({ kind: 'refused', reason: 'empty' })
  })

  it('trata falha de leitura do disco como recusa local', async () => {
    const file = fileOf(1, 'image/png')
    Object.defineProperty(file, 'arrayBuffer', {
      value: () => Promise.reject(new DOMException('gone', 'NotReadableError')),
    })

    expect(await readPhotoFile(file)).toEqual({ kind: 'refused', reason: 'unreadable' })
  })
})
