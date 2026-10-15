import { useId } from 'react'
import { cn } from 'cn'
import { useI18n } from '@/shared/i18n/i18n-context'
import { LOCALE_AUTONYMS, SUPPORTED_LOCALES } from '@/shared/i18n/locale'

/**
 * Seletor de idioma do cabeçalho: um grupo rotulado de botões toggle, um por idioma. O idioma
 * ativo tem `aria-pressed` e destaque que não depende só da cor (peso, fundo e borda). Cada nome
 * aparece no próprio idioma, marcado com `lang` para o leitor de tela pronunciar certo.
 */
export function LanguageSwitcher(): React.JSX.Element {
  const { locale, t, changeLocale } = useI18n()
  const labelId = useId()

  return (
    <div role="group" aria-labelledby={labelId} className="flex items-center gap-2">
      <span id={labelId} className="sr-only text-sm text-text-muted sm:not-sr-only">
        {t('app.languageLabel')}
      </span>
      {SUPPORTED_LOCALES.map((option) => {
        const active = option === locale
        return (
          <button
            key={option}
            type="button"
            lang={option}
            aria-pressed={active}
            onClick={() => {
              changeLocale(option)
            }}
            className={cn(
              'inline-flex min-h-11 min-w-11 items-center justify-center rounded-md border px-3 text-sm',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus',
              active
                ? 'border-border bg-surface-muted font-semibold text-text'
                : 'border-transparent text-action underline underline-offset-4 hover:bg-surface-muted',
            )}
          >
            {LOCALE_AUTONYMS[option]}
          </button>
        )
      })}
    </div>
  )
}
