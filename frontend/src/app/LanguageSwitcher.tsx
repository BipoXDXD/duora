import { QueryClientContext } from '@tanstack/react-query'
import { fetchLocale, saveLocale } from '@/features/preferences/preferences-api'
import { meQueryKey } from '@/features/session/query-keys'
import type { Me } from '@/features/session/session-api'
import { classifyRequestFailure } from '@/shared/api'
import { RequestFailureAlert } from '@/shared/ui/alert'
import type { Locale } from '@/shared/i18n/locale'
import { useContext, useId, useState } from 'react'
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
  const queries = useContext(QueryClientContext)
  const [pending, setPending] = useState(false)
  const [failure, setFailure] = useState<unknown>(null)
  async function select(next: Locale): Promise<void> {
    const account = queries?.getQueryData<Me>(meQueryKey)
    if (account === undefined) {
      changeLocale(next)
      return
    }
    if (pending) return
    setPending(true)
    setFailure(null)
    try {
      const current = await fetchLocale()
      if (queries?.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      const saved = await saveLocale(current.version, next)
      if (queries?.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      queries.setQueryData<Me>(meQueryKey, (value) =>
        value === undefined ? value : { ...value, preferredLocale: saved.locale },
      )
      void queries.invalidateQueries({ queryKey: ['account', account.id, 'locale'] })
      changeLocale(saved.locale)
    } catch (error) {
      setFailure(error)
    } finally {
      setPending(false)
    }
  }

  return (
    <>
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
              disabled={pending}
              onClick={() => {
                void select(option)
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
      {failure === null ? null : <RequestFailureAlert failure={classifyRequestFailure(failure)} />}
    </>
  )
}
