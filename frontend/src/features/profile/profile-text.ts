/**
 * Espelho de ProfileText.cs (backend): mesmas classes recusadas, mesma ordem e mesma contagem, para o erro aparecer
 * no navegador antes do envio. Divergência é bug; o servidor continua sendo a regra.
 */

export type TextProblem = 'empty' | 'tooLong' | 'forbiddenCharacter'

const MAX_MARKS_PER_BASE = 4

// Antes da NFC: surrogate solto, não atribuído e uso privado (os noncharacters são testados à parte).
const UNSAFE_TO_NORMALIZE = /[\p{Cs}\p{Cn}\p{Co}]/u
// Depois da NFC: controle, formatação (bidi, largura zero, soft hyphen), separadores de linha e parágrafo.
const FORBIDDEN = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\p{Cs}\p{Cn}\p{Co}]/u
const MARK = /[\p{Mn}\p{Me}]/u
// Igual a char.IsWhiteSpace do .NET: inclui NEL (U+0085) e não inclui U+FEFF, ao contrário do trim() do JavaScript.
const DOTNET_WHITESPACE = /[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]/u
const DOTNET_EDGES = new RegExp(`^${DOTNET_WHITESPACE.source}+|${DOTNET_WHITESPACE.source}+$`, 'gu')
const BLANK_FILLERS: ReadonlySet<number> = new Set([0x115f, 0x1160, 0x3164, 0xffa0, 0x2800])
const LINE_BREAKS: ReadonlySet<string> = new Set(['\n', '\r', '\t'])

function isNoncharacter(codePoint: number): boolean {
  return (codePoint >= 0xfdd0 && codePoint <= 0xfdef) || (codePoint & 0xfffe) === 0xfffe
}

/** Seletores de variação (FE00–FE0F, E0100–E01EF, FVS1) e CGJ: modificam a base anterior. */
function isSelector(codePoint: number): boolean {
  return (
    (codePoint >= 0xfe00 && codePoint <= 0xfe0f) ||
    (codePoint >= 0xe0100 && codePoint <= 0xe01ef) ||
    codePoint === 0x180b ||
    codePoint === 0x034f
  )
}

function isWhiteSpace(character: string): boolean {
  return DOTNET_WHITESPACE.test(character)
}

/** Code points, não grafemas: é a unidade do backend (runes) e do char_length do PostgreSQL. */
function codePoints(value: string): string[] {
  return Array.from(value)
}

/** Texto cru seguro para normalizar? Mesma checagem que o backend faz antes do Normalize. */
export function isSafeToNormalize(value: string): boolean {
  return codePoints(value).every(
    (character) => !UNSAFE_TO_NORMALIZE.test(character) && !isNoncharacter(character.codePointAt(0) ?? 0),
  )
}

/** NFC e bordas aparadas como o string.Trim do .NET; texto inseguro fica como veio, para ser recusado depois. */
export function normalizeProfileText(value: string): string {
  return (isSafeToNormalize(value) ? value.normalize('NFC') : value).replace(DOTNET_EDGES, '')
}

/** Base é o que não é marca, seletor, branco nem quebra; marca e seletor exigem uma base antes. */
function hasMisplacedMark(characters: readonly string[]): boolean {
  let hasBase = false
  let lastWasBase = false
  let selectorUsed = false
  let marks = 0
  for (const character of characters) {
    const codePoint = character.codePointAt(0) ?? 0
    if (isSelector(codePoint)) {
      if (!lastWasBase || selectorUsed) {
        return true
      }
      selectorUsed = true
      lastWasBase = false
    } else if (MARK.test(character)) {
      marks += 1
      lastWasBase = false
      if (!hasBase || marks > MAX_MARKS_PER_BASE) {
        return true
      }
    } else {
      hasBase = !isWhiteSpace(character)
      lastWasBase = hasBase
      selectorUsed = false
      marks = 0
    }
  }
  return false
}

function isVisible(character: string): boolean {
  const codePoint = character.codePointAt(0) ?? 0
  return !isWhiteSpace(character) && !MARK.test(character) && !isSelector(codePoint) && !BLANK_FILLERS.has(codePoint)
}

/** Problema de um texto já normalizado, na mesma ordem do backend: caractere, vazio, tamanho. */
export function findTextProblem(
  normalized: string,
  options: { readonly maxLength: number; readonly allowLineBreaks: boolean },
): TextProblem | null {
  if (!isSafeToNormalize(normalized)) {
    return 'forbiddenCharacter'
  }
  const characters = codePoints(normalized)
  const forbidden = characters.some(
    (character) =>
      !(options.allowLineBreaks && LINE_BREAKS.has(character)) &&
      (FORBIDDEN.test(character) || isNoncharacter(character.codePointAt(0) ?? 0)),
  )
  if (forbidden || hasMisplacedMark(characters)) {
    return 'forbiddenCharacter'
  }
  if (!characters.some(isVisible)) {
    return 'empty'
  }
  return characters.length > options.maxLength ? 'tooLong' : null
}
