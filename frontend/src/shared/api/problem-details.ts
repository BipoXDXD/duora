/**
 * Problem Details (RFC 9457) com as extensões do contrato do Duora (spec 8.1 e 8.6):
 * `code` estável para o frontend decidir comportamento e `traceId` para suporte.
 * O frontend traduz `code`; nunca interpreta `title` ou `detail` para decidir algo.
 */
export interface ProblemDetails {
  readonly type: string
  readonly title: string
  readonly status: number
  readonly code?: string
  readonly traceId?: string
  readonly detail?: string
  readonly errors?: Readonly<Record<string, readonly string[]>>
}

const DEFAULT_PROBLEM_TYPE = 'about:blank'

/** Lê um corpo problem+json; devolve null quando ele não respeita o contrato mínimo. */
export function parseProblemDetails(raw: unknown, httpStatus: number): ProblemDetails | null {
  if (!isRecord(raw) || typeof raw.title !== 'string') {
    return null
  }

  const code = optionalString(raw.code)
  const traceId = optionalString(raw.traceId)
  const detail = optionalString(raw.detail)
  const errors = parseFieldErrors(raw.errors)

  return {
    type: optionalString(raw.type) ?? DEFAULT_PROBLEM_TYPE,
    title: raw.title,
    // O status da resposta HTTP é a fonte da verdade; o do corpo é só informativo.
    status: httpStatus,
    ...(code === undefined ? {} : { code }),
    ...(traceId === undefined ? {} : { traceId }),
    ...(detail === undefined ? {} : { detail }),
    ...(errors === undefined ? {} : { errors }),
  }
}

function parseFieldErrors(raw: unknown): Record<string, readonly string[]> | undefined {
  if (!isRecord(raw)) {
    return undefined
  }
  const entries = Object.entries(raw).filter((entry): entry is [string, string[]] => isStringArray(entry[1]))
  return entries.length === 0 ? undefined : Object.fromEntries(entries)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
}

function optionalString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined
}
