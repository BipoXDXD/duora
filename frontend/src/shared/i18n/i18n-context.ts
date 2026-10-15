import { createContext, useContext } from 'react'
import type { Locale } from './locale'
import type { Translate } from './translator'

export interface I18n {
  readonly locale: Locale
  readonly t: Translate
  /** Instante (UTC na API) em data e hora curtas, no idioma ativo e no fuso de quem usa. */
  readonly formatDateTime: (instant: Date) => string
  /** Troca o idioma da interface e guarda a escolha como preferência. */
  readonly changeLocale: (locale: Locale) => void
}

export const I18nContext = createContext<I18n | null>(null)

/** Idioma ativo e seus formatadores; só existe abaixo de I18nProvider. */
export function useI18n(): I18n {
  const i18n = useContext(I18nContext)
  if (i18n === null) {
    throw new Error('useI18n usado fora de I18nProvider.')
  }
  return i18n
}
