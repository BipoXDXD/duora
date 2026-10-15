import type { Locale } from '@/shared/i18n/locale'
import { ANTIFORGERY_HEADER_NAME } from './antiforgery'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'
import { parseProblemDetails } from './problem-details'
import { parseRetryAfterSeconds } from './retry-after'

/** Assinatura aceita pelo `fetch` do openapi-fetch quando o contrato OpenAPI existir. */
export type ApiFetch = (request: Request) => Promise<Response>

export type HttpMethod = 'GET' | 'HEAD' | 'OPTIONS' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface JsonRequest {
  readonly method: HttpMethod
  /** Caminho absoluto na mesma origem, por exemplo `/api/v1/me`. */
  readonly path: string
  readonly body?: unknown
  readonly signal?: AbortSignal
}

export interface ApiClient {
  /** Executa a requisição e devolve o corpo JSON ainda não validado (ou undefined em 204). */
  requestJson(request: JsonRequest): Promise<unknown>
  /** Executa um comando cujo sucesso não traz dado (202/204) e descarta o corpo da resposta. */
  sendCommand(request: JsonRequest): Promise<void>
  /** Mesmas garantias, no formato de `fetch`, para plugar num cliente gerado. */
  readonly fetch: ApiFetch
}

export interface ApiClientDependencies {
  readonly fetch: ApiFetch
  readonly origin: string
  readonly readAntiforgeryToken: () => string | null
  /** Idioma ativo da interface, lido a cada requisição; o backend escreve os e-mails nele. */
  readonly readLocale: () => Locale
  /** Relógio em milissegundos desde a época, para interpretar Retry-After com data. */
  readonly now: () => number
}

const SAFE_METHODS: ReadonlySet<string> = new Set(['GET', 'HEAD', 'OPTIONS', 'TRACE'])
const NO_CONTENT = 204

/**
 * Único ponto de saída HTTP da SPA. Garante same-origin com cookies, header antiforgery em
 * mutações, idioma ativo no Accept-Language, cancelamento por AbortSignal e erro tipado para
 * qualquer resposta não-ok.
 * Não faz retry: repetir é decisão de quem chama (TanStack Query só repete leituras).
 */
export function createApiClient(dependencies: ApiClientDependencies): ApiClient {
  const send: ApiFetch = async (request) => {
    assertSameOrigin(request.url, dependencies.origin)
    const response = await fetchOrThrowNetworkError(dependencies.fetch, withSessionPolicy(request, dependencies))
    if (!response.ok) {
      throw await toResponseError(response, dependencies.now())
    }
    return response
  }

  const toRequest = ({ method, path, body, signal }: JsonRequest): Request =>
    new Request(resolveSameOriginUrl(path, dependencies.origin), {
      method,
      ...(body === undefined ? {} : { body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }),
      ...(signal === undefined ? {} : { signal }),
    })

  return {
    fetch: send,
    async requestJson(request) {
      return readJsonBody(await send(toRequest(request)))
    },
    async sendCommand(request) {
      const response = await send(toRequest(request))
      await response.body?.cancel()
    },
  }
}

function withSessionPolicy(request: Request, dependencies: ApiClientDependencies): Request {
  const headers = new Headers(request.headers)
  // O navegador mandaria a preferência dele, não a escolhida no seletor de idioma.
  headers.set('Accept-Language', dependencies.readLocale())
  if (!SAFE_METHODS.has(request.method.toUpperCase())) {
    const token = dependencies.readAntiforgeryToken()
    if (token !== null) {
      headers.set(ANTIFORGERY_HEADER_NAME, token)
    }
  }
  return new Request(request, { credentials: 'same-origin', headers })
}

async function fetchOrThrowNetworkError(fetch: ApiFetch, request: Request): Promise<Response> {
  try {
    return await fetch(request)
  } catch (error) {
    if (request.signal.aborted) {
      throw error
    }
    throw new ApiNetworkError(error)
  }
}

async function toResponseError(response: Response, nowMs: number): Promise<Error> {
  const retryAfterSeconds = parseRetryAfterSeconds(response.headers.get('Retry-After'), nowMs)
  if (mediaTypeOf(response) !== 'application/problem+json') {
    return new ApiResponseError(response.status, 'erro sem Problem Details', retryAfterSeconds)
  }
  const raw = await readJsonOrNull(response)
  const problem = parseProblemDetails(raw, response.status)
  return problem === null
    ? new ApiResponseError(response.status, 'Problem Details inválido', retryAfterSeconds)
    : new ApiProblemError(problem, retryAfterSeconds)
}

async function readJsonBody(response: Response): Promise<unknown> {
  if (response.status === NO_CONTENT || response.headers.get('Content-Length') === '0') {
    return undefined
  }
  if (mediaTypeOf(response) !== 'application/json') {
    throw new ApiResponseError(response.status, 'sucesso sem corpo JSON')
  }
  const body: unknown = await response.json()
  return body
}

async function readJsonOrNull(response: Response): Promise<unknown> {
  try {
    const body: unknown = await response.json()
    return body
  } catch {
    // Corpo malformado vira ApiResponseError em quem chama; o conteúdo não é exposto.
    return null
  }
}

function mediaTypeOf(response: Response): string {
  const contentType = response.headers.get('Content-Type') ?? ''
  return (contentType.split(';')[0] ?? '').trim().toLowerCase()
}

function resolveSameOriginUrl(path: string, origin: string): URL {
  if (!path.startsWith('/') || path.startsWith('//')) {
    throw new Error(`A API só aceita caminhos absolutos na mesma origem; recebido "${path}".`)
  }
  return new URL(path, origin)
}

function assertSameOrigin(url: string, origin: string): void {
  if (new URL(url).origin !== new URL(origin).origin) {
    throw new Error('A API só aceita requisições para a mesma origem da SPA.')
  }
}
