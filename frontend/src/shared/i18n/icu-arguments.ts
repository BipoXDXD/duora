/*
 * Argumentos de uma mensagem ICU (MessageFormat 1), calculados do texto literal no catálogo.
 * Assim `t('chave', valores)` só compila com exatamente os valores que a mensagem usa, sem
 * declarar os tipos à mão em outro lugar. Os valores em runtime são formatados pelo
 * intl-messageformat; isto só descreve a forma que ele espera.
 *
 * O parser percorre as chaves com uma pilha: no texto de uma mensagem, `{` abre um argumento;
 * dentro das opções de um plural/select, `{` abre o corpo de uma opção, que é outra mensagem.
 * Não trata chaves escapadas com apóstrofo ('{'); o catálogo não as usa.
 */

type Whitespace = ' ' | '\n' | '\t'

type Trim<S extends string> = S extends `${Whitespace}${infer Rest}`
  ? Trim<Rest>
  : S extends `${infer Rest}${Whitespace}`
    ? Trim<Rest>
    : S

/** Tipo aceito por argumento conforme o formato ICU (`{x}`, `{x, number}`, `{x, plural, …}`…). */
type ArgumentValue<Format extends string> = Format extends 'number' | 'plural' | 'selectordinal'
  ? number
  : Format extends 'date' | 'time'
    ? Date | number
    : Format extends 'select'
      ? string
      : string | number

/** `options`: entre as opções de plural/select. `body`: dentro do texto de uma opção. */
type Frame = 'options' | 'body'

type Argument = readonly [name: string, value: unknown]

/** A próxima chave do texto, `{` ou `}`, e o que vem depois dela. */
type NextBrace<S extends string> = S extends `${infer Before}{${infer AfterOpen}`
  ? Before extends `${string}}${infer AfterClose}`
    ? ['}', `${AfterClose}{${AfterOpen}`]
    : ['{', AfterOpen]
  : S extends `${string}}${infer AfterClose}`
    ? ['}', AfterClose]
    : null

type Parse<S extends string, Stack extends readonly Frame[], Found extends Argument> =
  NextBrace<S> extends [infer Brace, infer Rest extends string]
    ? Brace extends '{'
      ? Stack extends readonly [...Frame[], 'options']
        ? Parse<Rest, [...Stack, 'body'], Found>
        : ParseArgument<Rest, Stack, Found>
      : Parse<Rest, Pop<Stack>, Found>
    : Found

type Pop<Stack extends readonly Frame[]> = Stack extends readonly [...infer Init extends Frame[], Frame] ? Init : []

/** Logo depois de um `{` no texto de uma mensagem: `nome}`, `nome, formato[, estilo]}` ou plural/select. */
type ParseArgument<
  S extends string,
  Stack extends readonly Frame[],
  Found extends Argument,
> = S extends `${infer Head}}${infer After}`
  ? Head extends `${string}{${string}`
    ? ParseComplexArgument<S, Stack, Found>
    : Parse<After, Stack, Found | SimpleArgument<Head>>
  : Found

type SimpleArgument<Head extends string> = Head extends `${infer Name},${infer Format}`
  ? readonly [Trim<Name>, ArgumentValue<Trim<Format extends `${infer Type},${string}` ? Type : Format>>]
  : readonly [Trim<Head>, ArgumentValue<''>]

type ParseComplexArgument<
  S extends string,
  Stack extends readonly Frame[],
  Found extends Argument,
> = S extends `${infer Name},${infer Format},${infer Options}`
  ? Parse<Options, [...Stack, 'options'], Found | readonly [Trim<Name>, ArgumentValue<Trim<Format>>]>
  : Found

type ArgumentsObject<Found extends Argument> = { readonly [Entry in Found as Entry[0]]: Entry[1] }

/** Valores que a mensagem `Message` exige, por nome; `never` quando ela não tem argumentos. */
export type IcuArguments<Message extends string> = string extends Message
  ? never
  : [Parse<Message, [], never>] extends [never]
    ? never
    : ArgumentsObject<Parse<Message, [], never>>
