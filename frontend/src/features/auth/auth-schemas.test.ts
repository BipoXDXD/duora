// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { emailSchema, newPasswordSchema, readEmailLinkFragment } from './auth-schemas'

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'

describe('newPasswordSchema', () => {
  it.each([
    [11, false],
    [12, true],
    [128, true],
    [129, false],
  ])('senha com %i caracteres é válida? %s', (length, valid) => {
    expect(newPasswordSchema.safeParse('a'.repeat(length)).success).toBe(valid)
  })

  it('não apara espaços: doze espaços contam como doze caracteres', () => {
    expect(newPasswordSchema.parse(' '.repeat(12))).toBe(' '.repeat(12))
  })

  it('aceita qualquer composição, sem exigir dígito ou símbolo', () => {
    expect(newPasswordSchema.safeParse('cavalo correto bateria').success).toBe(true)
  })
})

describe('emailSchema', () => {
  it('apara espaços colados junto do endereço', () => {
    expect(emailSchema.parse('  ana@exemplo.com ')).toBe('ana@exemplo.com')
  })

  it.each(['', '   ', 'ana', 'ana@', `${'a'.repeat(250)}@x.co`])('recusa "%s"', (value) => {
    expect(emailSchema.safeParse(value).success).toBe(false)
  })
})

describe('readEmailLinkFragment', () => {
  it('lê userId e token do fragmento do link', () => {
    expect(readEmailLinkFragment(`#userId=${USER_ID}&token=${encodeURIComponent('abc+/=')}`)).toEqual({
      userId: USER_ID,
      token: 'abc+/=',
    })
  })

  it.each([
    ['fragmento vazio', ''],
    ['só o #', '#'],
    ['sem token', `userId=${USER_ID}`],
    ['token vazio', `userId=${USER_ID}&token=`],
    ['userId que não é UUID', 'userId=1&token=abc'],
    ['token gigante', `userId=${USER_ID}&token=${'a'.repeat(2049)}`],
  ])('devolve null para link inválido: %s', (_caso, query) => {
    expect(readEmailLinkFragment(`#${query}`)).toBeNull()
  })
})
