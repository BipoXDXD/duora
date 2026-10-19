import { afterEach, describe, expect, it, vi } from 'vitest'
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

  it('a tag antiga en, de antes do ADR-0026, migra para en-US e é regravada', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en')

    expect(loadLocalePreference()).toBe('en-US')
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en-US')
  })

  it('pt-BR guardado continua como está', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'pt-BR')

    expect(loadLocalePreference()).toBe('pt-BR')
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('pt-BR')
  })

  it('migra a tag antiga mesmo se a regravação falhar', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en')
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('cheio', 'QuotaExceededError')
    })

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
