// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { DISPLAY_NAME_MAX_LENGTH, BIO_MAX_LENGTH, profileFormSchema, toProfileChanges } from './profile-form'

function errorsOf(input: { displayName: string; bio: string }): Record<string, string | undefined> {
  const result = profileFormSchema.safeParse(input)
  if (result.success) {
    return {}
  }
  return Object.fromEntries(result.error.issues.map((issue) => [String(issue.path[0]), issue.message]))
}

describe('profileFormSchema', () => {
  it('aceita nome e descrição comuns, aparando as bordas', () => {
    expect(profileFormSchema.parse({ displayName: '  Ana  ', bio: ' Oi \n' })).toEqual({
      displayName: 'Ana',
      bio: 'Oi',
    })
  })

  it('normaliza em NFC antes de medir, como o servidor', () => {
    const decomposed = 'e\u0301'.repeat(DISPLAY_NAME_MAX_LENGTH)

    expect(profileFormSchema.parse({ displayName: decomposed, bio: '' }).displayName).toBe('é'.repeat(40))
  })

  it('conta caracteres Unicode, não unidades UTF-16', () => {
    expect(errorsOf({ displayName: '😀'.repeat(DISPLAY_NAME_MAX_LENGTH), bio: '' })).toEqual({})
    expect(errorsOf({ displayName: 'a'.repeat(DISPLAY_NAME_MAX_LENGTH + 1), bio: '' })).toEqual({
      displayName: 'profile.error.displayNameTooLong',
    })
  })

  it.each(['', '   ', '\u3000'])('exige nome de exibição (%j)', (displayName) => {
    expect(errorsOf({ displayName, bio: '' })).toEqual({ displayName: 'profile.error.displayNameRequired' })
  })

  it('aceita a descrição no limite e recusa acima dele', () => {
    expect(errorsOf({ displayName: 'Ana', bio: 'b'.repeat(BIO_MAX_LENGTH) })).toEqual({})
    expect(errorsOf({ displayName: 'Ana', bio: 'b'.repeat(BIO_MAX_LENGTH + 1) })).toEqual({
      bio: 'profile.error.bioTooLong',
    })
  })

  it.each([
    ['NUL', '\u0000'],
    ['controle', '\u0007'],
    ['quebra de linha no nome', '\n'],
    ['RLO', '\u202E'],
    ['LRM', '\u200E'],
    ['largura zero', '\u200B'],
    ['ZWJ', '\u200D'],
    ['BOM', '\uFEFF'],
    ['separador de linha', '\u2028'],
    ['surrogate solto', '\uD800'],
  ])('recusa %s no nome', (_classe, character) => {
    expect(errorsOf({ displayName: `Ana${character}Souza`, bio: '' })).toEqual({
      displayName: 'profile.error.invalidCharacters',
    })
  })

  it('aceita quebra de linha e tabulação na descrição, mas não outros controles', () => {
    expect(errorsOf({ displayName: 'Ana', bio: 'Linha 1\nLinha 2\tfim' })).toEqual({})
    expect(errorsOf({ displayName: 'Ana', bio: 'Oi\u001B[31m' })).toEqual({ bio: 'profile.error.invalidCharacters' })
    expect(errorsOf({ displayName: 'Ana', bio: 'Oi\u200Btudo' })).toEqual({ bio: 'profile.error.invalidCharacters' })
  })
})

describe('profileFormSchema: mesma estrutura que o servidor (revisão do PR #24)', () => {
  it.each([
    ['noncharacter U+FDD0', '\uFDD0'],
    ['noncharacter U+FFFE', '\uFFFE'],
    ['noncharacter U+FFFF', '\uFFFF'],
    ['noncharacter U+1FFFE', '\u{1FFFE}'],
    ['noncharacter U+10FFFF', '\u{10FFFF}'],
    ['não atribuído U+0378', '\u0378'],
    ['uso privado U+E000', '\uE000'],
    ['uso privado U+F0000', '\u{F0000}'],
  ])('recusa %s no nome e na descrição', (_classe, character) => {
    expect(errorsOf({ displayName: `Ana${character}`, bio: `Oi${character}` })).toEqual({
      displayName: 'profile.error.invalidCharacters',
      bio: 'profile.error.invalidCharacters',
    })
  })

  it.each([
    ['começa com Mn', '\u0301Ana'],
    ['começa com Me', '\u20DDAna'],
    ['cinco Mn na mesma base', 'x\u0332\u0332\u0332\u0332\u0332'],
    ['começa com seletor', '\uFE0FAna'],
    ['dois seletores na mesma base', '\u263A\uFE0F\uFE0E'],
    ['seletor depois de marca', 'x\u0332\uFE0F'],
    ['começa com CGJ', '\u034FAna'],
    ['dois CGJ', 'a\u034F\u034F'],
    ['seletor depois de espaço', 'Ana \uFE0Fx'],
    ['seletor suplementar depois de letra latina', 'a\u{E0100}'],
    ['seletor suplementar depois de emoji', '\u263A\u{E0100}'],
  ])('recusa marca fora do lugar: %s', (_motivo, displayName) => {
    expect(errorsOf({ displayName, bio: '' })).toEqual({ displayName: 'profile.error.invalidCharacters' })
  })

  it('recusa marca no começo de uma linha da descrição', () => {
    expect(errorsOf({ displayName: 'Ana', bio: 'Oi\n\u0301tudo' })).toEqual({ bio: 'profile.error.invalidCharacters' })
  })

  it.each([
    ['quatro Mn na mesma base', 'x\u0332\u0332\u0332\u0332'],
    ['emoji com VS16', '\u263A\uFE0F'],
    ['letra com VS15', 'Ana\uFE0E'],
    ['ideograma com seletor suplementar', '\u845B\u{E0100}'],
    ['ideograma da extensão B com seletor suplementar', '\u{2000B}\u{E0101}'],
    ['mongol com FVS1', '\u1820\u180B'],
    ['CGJ depois da base', 'a\u034Fb'],
  ])('aceita marca logo depois da base: %s', (_motivo, displayName) => {
    expect(errorsOf({ displayName, bio: '' })).toEqual({})
  })

  it.each(['\u3164', '\u115F\u1160', '\uFFA0', '\u2800', ' \u3164 \u2800 '])(
    'nome só de fillers é vazio (%j)',
    (value) => {
      expect(errorsOf({ displayName: value, bio: '' })).toEqual({ displayName: 'profile.error.displayNameRequired' })
    },
  )

  it('descrição só de fillers é vazia e vira null', () => {
    expect(profileFormSchema.parse({ displayName: 'Ana', bio: '\u3164\u2800' }).bio).toBe('')
  })

  it('apara as bordas como o .NET: NEL sai, U+FEFF fica e é recusado', () => {
    expect(profileFormSchema.parse({ displayName: '\u0085Ana\u0085', bio: '' }).displayName).toBe('Ana')
    expect(errorsOf({ displayName: '\uFEFFAna', bio: '' })).toEqual({ displayName: 'profile.error.invalidCharacters' })
  })
})

describe('toProfileChanges', () => {
  const values = { displayName: 'Ana', bio: 'Oi' }

  it('manda só os campos alterados', () => {
    expect(toProfileChanges(values, { displayName: true })).toEqual({ displayName: 'Ana' })
    expect(toProfileChanges(values, { bio: true })).toEqual({ bio: 'Oi' })
    expect(toProfileChanges(values, { displayName: true, bio: true })).toEqual({ displayName: 'Ana', bio: 'Oi' })
  })

  it('sem campo alterado o corpo vai vazio', () => {
    expect(toProfileChanges(values, {})).toEqual({})
  })

  it('descrição apagada vira null, que apaga no servidor', () => {
    expect(toProfileChanges({ displayName: 'Ana', bio: '' }, { bio: true })).toEqual({ bio: null })
  })
})
