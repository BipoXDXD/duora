import { useMemo, type ReactNode } from 'react'
import { I18nContext, useI18n } from './i18n-context'
import { createDateTimeFormatter, createEventDateTimeFormatter } from './translator'

/** Escopo da identidade atual: sair ou trocar de conta desmonta a preferência, sem cache global. */
export function AccountTimeZoneProvider({
  timeZone,
  children,
}: {
  readonly timeZone: string | null
  readonly children: ReactNode
}): React.JSX.Element {
  const parent = useI18n()
  const value = useMemo(() => {
    let formatDateTime = parent.formatDateTime
    if (timeZone !== null) {
      try {
        formatDateTime = createDateTimeFormatter(parent.locale, timeZone)
      } catch (error) {
        if (!(error instanceof RangeError)) throw error
        // Instantes genéricos continuam explicitamente UTC quando o ICU não reconhece a escolha salva.
        formatDateTime = createDateTimeFormatter(parent.locale, 'UTC')
      }
    }
    return {
      ...parent,
      formatDateTime,
      formatEventDateTime: createEventDateTimeFormatter(
        parent.locale,
        timeZone ?? undefined,
        timeZone === null ? 'browser' : 'preference',
      ),
    }
  }, [parent, timeZone])
  return <I18nContext value={value}>{children}</I18nContext>
}
