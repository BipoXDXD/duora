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

/** Canonicaliza aliases, tolerando apenas fusos ausentes no ICU deste dispositivo. */
function canonicalZone(locale: Locale, zone: string): string | null {
  try {
    return new Intl.DateTimeFormat(locale, { timeZone: zone }).resolvedOptions().timeZone
  } catch (error) {
    if (error instanceof RangeError) return null
    throw error
  }
}

/** Nome geográfico quando disponível, sempre acompanhado pelo deslocamento deste instante. */
function createEventZoneFormatter(locale: Locale, timeZone: string): (instant: Date) => string {
  const fields: Intl.DateTimeFormatOptions = {
    year: locale === 'en-US' ? '2-digit' : 'numeric',
    month: locale === 'en-US' ? 'numeric' : '2-digit',
    day: locale === 'en-US' ? 'numeric' : '2-digit',
    hour: locale === 'en-US' ? 'numeric' : '2-digit',
    minute: '2-digit',
    timeZone,
  }
  const date = new Intl.DateTimeFormat(locale, fields)
  const generic = new Intl.DateTimeFormat(locale, { ...fields, timeZoneName: 'shortGeneric' })
  const offset = new Intl.DateTimeFormat('en-US', { timeZone, timeZoneName: 'longOffset' })
  return (instant) => {
    const delta = offset.formatToParts(instant).find((part) => part.type === 'timeZoneName')?.value ?? 'GMT'
    const utc = delta.replace('GMT', 'UTC').replace('-', '−')
    if (timeZone === 'UTC') return `${date.format(instant)} UTC`
    const name = generic.formatToParts(instant).find((part) => part.type === 'timeZoneName')?.value ?? ''
    return /^(GMT|UTC)([+−-]|$)/u.test(name) || name === ''
      ? `${date.format(instant)} ${utc}`
      : `${date.format(instant)} — ${name} · ${utc}`
  }
}

/** O evento continua primário; a escolha explícita da conta tem precedência sobre o navegador. */
export function createEventDateTimeFormatter(
  locale: Locale,
  participantTimeZone: string = new Intl.DateTimeFormat().resolvedOptions().timeZone,
  source: 'browser' | 'preference' = 'browser',
): (instant: Date, eventTimeZone: string | null) => string {
  const t = createTranslator(locale)
  const participantZone = canonicalZone(locale, participantTimeZone)
  const cache = new Map<string, (instant: Date) => string>()
  const format = (instant: Date, zone: string): string => {
    let formatter = cache.get(zone)
    if (formatter === undefined) {
      formatter = createEventZoneFormatter(locale, zone)
      cache.set(zone, formatter)
    }
    return formatter(instant)
  }
  return (instant, eventTimeZone) => {
    const eventZone = canonicalZone(locale, eventTimeZone ?? 'UTC')
    const primary =
      eventZone === null
        ? t('dates.zoneUnsupported', { time: format(instant, 'UTC'), zone: eventTimeZone ?? 'UTC' })
        : t(eventTimeZone === null ? 'dates.eventZoneUnknown' : 'dates.eventTime', { time: format(instant, eventZone) })
    if (participantZone === null) {
      return t('dates.withUnsupportedParticipant', {
        event: primary,
        reference: t('dates.zoneUnsupported', { time: format(instant, 'UTC'), zone: participantTimeZone }),
      })
    }
    return participantZone === (eventZone ?? 'UTC')
      ? primary
      : t(source === 'preference' ? 'dates.withPreferredTime' : 'dates.withBrowserTime', {
          event: primary,
          local: format(instant, participantZone),
        })
  }
}
