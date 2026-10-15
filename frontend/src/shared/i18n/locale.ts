/** Idiomas da interface; o primeiro é o padrão (spec 13.6). */
export const SUPPORTED_LOCALES = ['pt-BR', 'en'] as const

export type Locale = (typeof SUPPORTED_LOCALES)[number]

export const DEFAULT_LOCALE: Locale = 'pt-BR'

/**
 * Nome de cada idioma nele mesmo (autônimo). Não se traduz: quem não lê o idioma atual precisa
 * reconhecer o próprio. O seletor marca cada nome com o `lang` dele (WCAG 3.1.2).
 */
export const LOCALE_AUTONYMS: Readonly<Record<Locale, string>> = {
  'pt-BR': 'Português',
  en: 'English',
}

// Qualquer variante regional cai no idioma suportado mais próximo: pt-PT lê melhor em pt-BR que em inglês.
const LOCALE_BY_LANGUAGE: Readonly<Partial<Record<string, Locale>>> = {
  pt: 'pt-BR',
  en: 'en',
}

/** Idioma suportado para uma tag BCP 47 (`en-US`, `pt`), ou `null` se nenhum serve. */
export function matchLocale(tag: string): Locale | null {
  const language = tag.split('-', 1)[0]?.toLowerCase() ?? ''
  return LOCALE_BY_LANGUAGE[language] ?? null
}

interface LocaleSources {
  /** Escolha guardada pela pessoa; vem do armazenamento do navegador, então é dado externo. */
  readonly stored: string | null
  /** `navigator.languages`, em ordem de preferência. */
  readonly preferred: readonly string[]
}

/** Idioma inicial: a escolha explícita vence; depois a ordem do navegador; por fim pt-BR. */
export function resolveInitialLocale({ stored, preferred }: LocaleSources): Locale {
  const storedLocale = SUPPORTED_LOCALES.find((locale) => locale === stored)
  if (storedLocale !== undefined) {
    return storedLocale
  }
  for (const tag of preferred) {
    const locale = matchLocale(tag)
    if (locale !== null) {
      return locale
    }
  }
  return DEFAULT_LOCALE
}
