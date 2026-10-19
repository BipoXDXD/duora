import { afterEach, describe, expect, it, vi } from 'vitest'
import { resolveInitialLocale } from './locale'
import { loadLocalePreference, LOCALE_STORAGE_KEY, saveLocalePreference } from './locale-preference'

afterEach(() => {
  window.localStorage.clear()
})

describe('preferência de idioma', () => {
  it('guarda e lê a escolha', () => {
    saveLocalePreference('en-US')

    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en-US')
    expect(loadLocalePreference()).toBe('en-US')
  })

  it('lê a preferência legada e grava a tag canônica na próxima escolha', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en')

    const locale = resolveInitialLocale({ stored: loadLocalePreference(), preferred: ['pt-BR'] })
    expect(locale).toBe('en-US')
    saveLocalePreference(locale)

    expect(loadLocalePreference()).toBe('en-US')
  })

  it('sem escolha guardada, devolve null', () => {
    expect(loadLocalePreference()).toBeNull()
  })

  it('armazenamento bloqueado na leitura não derruba a aplicação', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('bloqueado', 'SecurityError')
    })

    expect(loadLocalePreference()).toBeNull()
  })

  it('armazenamento cheio ou bloqueado na escrita não derruba a aplicação', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('cheio', 'QuotaExceededError')
    })

    expect(() => {
      saveLocalePreference('en-US')
    }).not.toThrow()
  })
})
