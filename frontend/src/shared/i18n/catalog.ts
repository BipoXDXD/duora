import { en } from './catalog.en'
import { ptBR } from './catalog.pt-BR'
import type { IcuArguments } from './icu-arguments'
import type { Locale } from './locale'

type SourceCatalog = typeof ptBR

/** Toda chave do catálogo de origem; uma chave fora dele não compila. */
export type MessageKey = keyof SourceCatalog

/** Forma de qualquer catálogo de idioma: exatamente as chaves do catálogo de origem. */
export type Catalog = Readonly<Record<MessageKey, string>>

/** Valores exigidos pela mensagem `Key`, deduzidos do texto ICU do catálogo de origem; `never` se não há. */
export type MessageValues<Key extends MessageKey> = IcuArguments<SourceCatalog[Key]>

/** Chaves cujas mensagens não têm argumentos: podem ser guardadas em tabelas e traduzidas sem valores. */
export type PlainMessageKey = {
  [Key in MessageKey]: [MessageValues<Key>] extends [never] ? Key : never
}[MessageKey]

/**
 * Chaves cuja tradução pede argumentos diferentes dos do catálogo de origem. Precisa ser `never`;
 * catalog.test.ts confere isso por tipo, e por runtime com o parser ICU.
 */
export type ArgumentMismatch<Translation extends Catalog> = {
  [Key in MessageKey]: [IcuArguments<Translation[Key]>] extends [MessageValues<Key>]
    ? [MessageValues<Key>] extends [IcuArguments<Translation[Key]>]
      ? never
      : Key
    : Key
}[MessageKey]

export const CATALOGS: Readonly<Record<Locale, Catalog>> = {
  'pt-BR': ptBR,
  en,
}
