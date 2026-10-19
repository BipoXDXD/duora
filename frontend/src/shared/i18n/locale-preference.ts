import type { Locale } from './locale'

export const LOCALE_STORAGE_KEY = 'duora.locale'

/*
 * O idioma é só preferência de apresentação, sem dado pessoal: perder a escolha custa um clique.
 * Por isso falhas do armazenamento (modo privado, cota, bloqueio de site) são ignoradas aqui,
 * e a interface segue com o idioma detectado. O valor lido é dado externo e passa por
 * resolveInitialLocale antes de virar Locale.
 */

/** Tags gravadas antes do ADR-0026 e a tag que as substitui. */
const LEGACY_LOCALES: Readonly<Partial<Record<string, Locale>>> = { en: 'en-US' }

export function loadLocalePreference(): string | null {
  let stored: string | null
  try {
    stored = window.localStorage.getItem(LOCALE_STORAGE_KEY)
  } catch {
    return null
  }
  const migrated = stored === null ? undefined : LEGACY_LOCALES[stored]
  if (migrated === undefined) {
    return stored
  }
  // Regrava a tag nova uma vez; se a escrita falhar, a migração refaz na próxima carga.
  saveLocalePreference(migrated)
  return migrated
}

export function saveLocalePreference(locale: Locale): void {
  try {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, locale)
  } catch {
    // Sem armazenamento, a escolha vale só nesta aba (ver comentário do módulo).
  }
}
