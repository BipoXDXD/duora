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
    const decomposed = 'é'.repeat(DISPLAY_NAME_MAX_LENGTH)

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

describe('toProfileChanges', () => {
  it('manda o nome e a descrição', () => {
    expect(toProfileChanges({ displayName: 'Ana', bio: 'Oi' })).toEqual({ displayName: 'Ana', bio: 'Oi' })
  })

  it('descrição vazia vira null, que apaga no servidor', () => {
    expect(toProfileChanges({ displayName: 'Ana', bio: '' })).toEqual({ displayName: 'Ana', bio: null })
  })
})
