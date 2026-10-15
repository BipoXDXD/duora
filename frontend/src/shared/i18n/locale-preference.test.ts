import { afterEach, describe, expect, it, vi } from 'vitest'
import { loadLocalePreference, LOCALE_STORAGE_KEY, saveLocalePreference } from './locale-preference'

afterEach(() => {
  window.localStorage.clear()
})

describe('preferência de idioma', () => {
  it('guarda e lê a escolha', () => {
    saveLocalePreference('en')

    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en')
    expect(loadLocalePreference()).toBe('en')
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
      saveLocalePreference('en')
    }).not.toThrow()
  })
})
