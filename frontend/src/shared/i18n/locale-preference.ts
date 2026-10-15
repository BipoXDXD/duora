import type { Locale } from './locale'

export const LOCALE_STORAGE_KEY = 'duora.locale'

/*
 * O idioma é só preferência de apresentação, sem dado pessoal: perder a escolha custa um clique.
 * Por isso falhas do armazenamento (modo privado, cota, bloqueio de site) são ignoradas aqui,
 * e a interface segue com o idioma detectado. O valor lido é dado externo e passa por
 * resolveInitialLocale antes de virar Locale.
 */

export function loadLocalePreference(): string | null {
  try {
    return window.localStorage.getItem(LOCALE_STORAGE_KEY)
  } catch {
    return null
  }
}

export function saveLocalePreference(locale: Locale): void {
  try {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, locale)
  } catch {
    // Sem armazenamento, a escolha vale só nesta aba (ver comentário do módulo).
  }
}
