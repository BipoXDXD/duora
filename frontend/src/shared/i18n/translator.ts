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
 * de quem usa (spec 13.6); `timeZone` só é passado em teste.
 */
export function createDateTimeFormatter(locale: Locale, timeZone?: string): (instant: Date) => string {
  const format = new Intl.DateTimeFormat(locale, {
    dateStyle: 'short',
    timeStyle: 'short',
    ...(timeZone === undefined ? {} : { timeZone }),
  })
  return (instant) => format.format(instant)
}
