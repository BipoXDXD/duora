import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { I18nContext, type I18n } from './i18n-context'
import type { Locale } from './locale'
import { saveLocalePreference } from './locale-preference'
import { createDateTimeFormatter, createTranslator } from './translator'

interface I18nProviderProps {
  /** Idioma da primeira renderização (ver resolveInitialLocale). */
  readonly initialLocale: Locale
  readonly children: ReactNode
}

/** Idioma ativo da interface; mantém o `<html lang>` em sincronia (WCAG 3.1.1). */
export function I18nProvider({ initialLocale, children }: I18nProviderProps): React.JSX.Element {
  const [locale, setLocale] = useState(initialLocale)

  useEffect(() => {
    document.documentElement.lang = locale
  }, [locale])

  const i18n = useMemo<I18n>(
    () => ({
      locale,
      t: createTranslator(locale),
      formatDateTime: createDateTimeFormatter(locale),
      changeLocale: (next) => {
        saveLocalePreference(next)
        setLocale(next)
      },
    }),
    [locale],
  )

  return <I18nContext value={i18n}>{children}</I18nContext>
}
