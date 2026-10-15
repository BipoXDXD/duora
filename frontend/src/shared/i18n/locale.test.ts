// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { matchLocale, resolveInitialLocale } from './locale'

describe('matchLocale', () => {
  it.each([
    ['pt-BR', 'pt-BR'],
    ['pt', 'pt-BR'],
    ['pt-PT', 'pt-BR'],
    ['PT-br', 'pt-BR'],
    ['en', 'en'],
    ['en-US', 'en'],
    ['en-GB', 'en'],
    ['EN-us', 'en'],
  ])('%s → %s', (tag, expected) => {
    expect(matchLocale(tag)).toBe(expected)
  })

  it.each(['fr-FR', 'es', 'de-DE', '', '-', 'english', 'p'])('"%s" não é suportado', (tag) => {
    expect(matchLocale(tag)).toBeNull()
  })
})

describe('resolveInitialLocale', () => {
  it('usa a escolha guardada antes do idioma do navegador', () => {
    expect(resolveInitialLocale({ stored: 'en', preferred: ['pt-BR'] })).toBe('en')
  })

  it('ignora escolha guardada fora da lista e cai para o navegador', () => {
    expect(resolveInitialLocale({ stored: 'xx', preferred: ['en-US'] })).toBe('en')
  })

  it('a escolha guardada precisa ser exata, não só parecida', () => {
    expect(resolveInitialLocale({ stored: 'en-US', preferred: ['pt-BR'] })).toBe('pt-BR')
  })

  it('sem escolha guardada, usa o primeiro idioma suportado na ordem do navegador', () => {
    expect(resolveInitialLocale({ stored: null, preferred: ['fr-FR', 'en-GB', 'pt-BR'] })).toBe('en')
  })

  it('sem idioma suportado no navegador, cai para pt-BR', () => {
    expect(resolveInitialLocale({ stored: null, preferred: ['fr-FR', 'de'] })).toBe('pt-BR')
  })

  it('sem nenhuma preferência, cai para pt-BR', () => {
    expect(resolveInitialLocale({ stored: null, preferred: [] })).toBe('pt-BR')
  })
})
