// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { readAntiforgeryTokenFromCookies } from './antiforgery'

describe('readAntiforgeryTokenFromCookies', () => {
  it('lê o token do cookie XSRF-TOKEN entre outros cookies', () => {
    expect(readAntiforgeryTokenFromCookies('theme=light; XSRF-TOKEN=abc%2B123; other=1')).toBe('abc+123')
  })

  it.each([
    ['sem cookies', ''],
    ['sem o cookie do token', 'theme=light'],
    ['cookie com nome parecido', 'X-XSRF-TOKEN-OLD=abc'],
    ['cookie vazio', 'XSRF-TOKEN='],
    ['cookie com percent-encoding malformado', 'XSRF-TOKEN=%E0%A4%A'],
  ])('devolve null quando %s', (_caso, cookies) => {
    expect(readAntiforgeryTokenFromCookies(cookies)).toBeNull()
  })
})
