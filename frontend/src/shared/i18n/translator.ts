import { IntlMessageFormat } from 'intl-messageformat'
import { CATALOGS, type MessageKey, type MessageValues } from './catalog'
import type { Locale } from './locale'

type TranslateValues<Key extends MessageKey> = [MessageValues<Key>] extends [never] ? [] : [values: MessageValues<Key>]

/** `t('chave')` ou `t('chave', valores)`: chave e valores são checados contra o catálogo de origem. */
export type Translate = <Key extends MessageKey>(key: Key, ...values: TranslateValues<Key>) => string

type IcuValues = Readonly<Record<string, string | number | Date>>

/** Tradutor de um idioma. Cada mensagem é analisada uma vez, na primeira vez que é usada. */
export function createTranslator(locale: Locale): Translate {
  const catalog = CATALOGS[locale]
  const formatters = new Map<MessageKey, IntlMessageFormat>()

  function formatterFor(key: MessageKey): IntlMessageFormat {
    const cached = formatters.get(key)
    if (cached !== undefined) {
      return cached
    }
    const formatter = new IntlMessageFormat(catalog[key], locale)
    formatters.set(key, formatter)
    return formatter
  }

  return (key: MessageKey, values?: IcuValues): string => {
    const formatted = formatterFor(key).format<string>(values)
    // Só há partes em lista quando a mensagem tem tags ricas, que o catálogo não usa.
    return Array.isArray(formatted) ? formatted.join('') : formatted
  }
}

/**
 * Data e hora curtas no padrão do idioma. Instantes chegam da API em UTC e são exibidos no fuso
 * do navegador quando omitido (spec 13.6). Não recebe datas civis: não converter YYYY-MM-DD em Date.
 */
export function createDateTimeFormatter(locale: Locale, timeZone?: string): (instant: Date) => string {
  const format = new Intl.DateTimeFormat(locale, {
    // dateStyle/timeStyle não podem ser combinados com timeZoneName.
    year: locale === 'en-US' ? '2-digit' : 'numeric',
    month: locale === 'en-US' ? 'numeric' : '2-digit',
    day: locale === 'en-US' ? 'numeric' : '2-digit',
    hour: locale === 'en-US' ? 'numeric' : '2-digit',
    minute: '2-digit',
    timeZoneName: 'short',
    ...(timeZone === undefined ? {} : { timeZone }),
  })
  return (instant) => format.format(instant)
}

/** Fuso do evento explícito; o fuso do navegador é contexto de exibição, não preferência da conta. */
export function createEventDateTimeFormatter(
  locale: Locale,
  participantTimeZone: string = new Intl.DateTimeFormat().resolvedOptions().timeZone,
): (instant: Date, eventTimeZone: string | null) => string {
  const t = createTranslator(locale)
  const participantZone = new Intl.DateTimeFormat(locale, { timeZone: participantTimeZone }).resolvedOptions().timeZone
  const participantFormat = createDateTimeFormatter(locale, participantZone)
  return (instant, eventTimeZone) => {
    const eventZone = new Intl.DateTimeFormat(locale, { timeZone: eventTimeZone ?? 'UTC' }).resolvedOptions().timeZone
    const event = createDateTimeFormatter(locale, eventZone)(instant)
    const primary = t(eventTimeZone === null ? 'dates.eventZoneUnknown' : 'dates.eventTime', { time: event })
    return participantZone === eventZone
      ? primary
      : t('dates.withBrowserTime', { event: primary, local: participantFormat(instant) })
  }
}
