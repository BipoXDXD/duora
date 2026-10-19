// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { matchLocale, resolveInitialLocale } from './locale'

describe('matchLocale', () => {
  it.each([
    ['pt-BR', 'pt-BR'],
    ['pt', 'pt-BR'],
    ['pt-PT', 'pt-BR'],
    ['PT-br', 'pt-BR'],
    ['en', 'en-US'],
    ['en-US', 'en-US'],
    ['en-GB', 'en-US'],
    ['EN-us', 'en-US'],
  ])('%s → %s', (tag, expected) => {
    expect(matchLocale(tag)).toBe(expected)
  })

  it.each(['fr-FR', 'es', 'de-DE', '', '-', 'english', 'p'])('"%s" não é suportado', (tag) => {
    expect(matchLocale(tag)).toBeNull()
  })
})

describe('resolveInitialLocale', () => {
  it('usa a escolha guardada antes do idioma do navegador', () => {
    expect(resolveInitialLocale({ stored: 'en-US', preferred: ['pt-BR'] })).toBe('en-US')
  })

  it('ignora escolha guardada fora da lista e cai para o navegador', () => {
    expect(resolveInitialLocale({ stored: 'xx', preferred: ['pt-PT'] })).toBe('pt-BR')
  })

  it('a escolha guardada precisa ser exata, não só parecida', () => {
    expect(resolveInitialLocale({ stored: 'en-GB', preferred: ['pt-BR'] })).toBe('pt-BR')
  })

  it('sem escolha guardada, usa o primeiro idioma suportado na ordem do navegador', () => {
    expect(resolveInitialLocale({ stored: null, preferred: ['fr-FR', 'en-GB', 'pt-BR'] })).toBe('en-US')
  })

  it('qualquer variante do português cai em pt-BR', () => {
    expect(resolveInitialLocale({ stored: null, preferred: ['fr-FR', 'pt-PT'] })).toBe('pt-BR')
  })

  // ADR-0026: o público principal não é brasileiro, então o fallback é en-US.
  it('sem idioma suportado no navegador, cai para en-US', () => {
    expect(resolveInitialLocale({ stored: null, preferred: ['fr-FR', 'de'] })).toBe('en-US')
  })

  it('sem nenhuma preferência, cai para en-US', () => {
    expect(resolveInitialLocale({ stored: null, preferred: [] })).toBe('en-US')
  })
})
