// @vitest-environment node
import { isStructurallySame, parse } from '@formatjs/icu-messageformat-parser'
import { describe, expect, expectTypeOf, it } from 'vitest'
import { CATALOGS, type ArgumentMismatch } from './catalog'
import { en } from './catalog.en-US'
import { ptBR } from './catalog.pt-BR'
import { SUPPORTED_LOCALES } from './locale'

const sourceKeys = Object.keys(ptBR).sort()

describe('catálogos', () => {
  it.each(SUPPORTED_LOCALES)('%s tem exatamente as chaves do catálogo de origem', (locale) => {
    expect(Object.keys(CATALOGS[locale]).sort()).toEqual(sourceKeys)
  })

  it('en usa os mesmos argumentos ICU, com o mesmo tipo, em cada mensagem', () => {
    const sourceMessages = new Map<string, string>(Object.entries(ptBR))
    const mismatches = Object.entries(en)
      .map(([key, message]) => ({
        key,
        result: isStructurallySame(parse(sourceMessages.get(key) ?? ''), parse(message)),
      }))
      .filter(({ result }) => !result.success)
      .map(({ key, result }) => `${key}: ${result.error?.message ?? ''}`)

    expect(mismatches).toEqual([])
  })

  it('a checagem por tipo também vê argumentos iguais', () => {
    expectTypeOf<ArgumentMismatch<typeof en>>().toBeNever()
  })

  it.each(SUPPORTED_LOCALES)('%s só tem mensagens ICU válidas', (locale) => {
    const invalid = Object.entries(CATALOGS[locale]).filter(([, message]) => {
      try {
        parse(message)
        return false
      } catch {
        return true
      }
    })

    expect(invalid).toEqual([])
  })

  it.each(SUPPORTED_LOCALES)('%s não tem mensagem vazia', (locale) => {
    const empty = Object.entries(CATALOGS[locale]).filter(([, message]) => message.trim() === '')

    expect(empty).toEqual([])
  })
})
