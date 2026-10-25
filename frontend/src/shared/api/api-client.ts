import type { Locale } from '@/shared/i18n/locale'
import {
  bindBootstrapRequest,
  bindPrivateRequest,
  bindPublicRequest,
  contextAbort,
  createSessionContextStore,
  type BootstrapTicket,
  type OperationHandle,
  type SessionContextStore,
} from '../session-context'
import { ANTIFORGERY_HEADER_NAME } from './antiforgery'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'
import { parseProblemDetails } from './problem-details'
import { parseRetryAfterSeconds } from './retry-after'
import { OwnedResponse } from './owned-response'
import { isLogoutRequest, isPublicAuthenticationMutation } from './request-purpose'

/** Assinatura aceita pelo `fetch` do openapi-fetch quando o contrato OpenAPI existir. */
export type ApiFetch = (request: Request) => Promise<Response>

export type HttpMethod = 'GET' | 'HEAD' | 'OPTIONS' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface JsonRequest {
  readonly method: HttpMethod
  /** Caminho absoluto na mesma origem, por exemplo `/api/v1/me`. */
  readonly path: string
  readonly body?: unknown
  /** Headers do recurso, como `If-Match`; o header antiforgery é sempre do cliente e não pode ser trocado. */
  readonly headers?: Readonly<Record<string, string>>
  readonly signal?: AbortSignal
  readonly operation?: OperationHandle
}

export interface ApiClient {
  /** Executa a requisição e devolve o corpo JSON ainda não validado (ou undefined em 204). */
  requestJson(request: JsonRequest): Promise<unknown>
  /** Executa um comando cujo sucesso não traz dado (202/204) e descarta o corpo da resposta. */
  sendCommand(request: JsonRequest): Promise<void>
  /** Entrada sem capacidade recusada; clientes brutos/gerados usam fetchFor(operation). */
  readonly fetch: ApiFetch
  /** Cliente gerado/bruto precisa de uma operação iniciada antes de qualquer await da intenção. */
  fetchFor(operation: OperationHandle): ApiFetch
  readonly sessionContext: SessionContextStore
  readMeBootstrap(
    ticket: BootstrapTicket,
    signal?: AbortSignal,
  ): Promise<{ readonly status: number; readonly body: unknown }>
}

export interface ApiClientDependencies {
  readonly fetch: ApiFetch
  readonly origin: string
  readonly readAntiforgeryToken: () => string | null
  /** Idioma ativo da interface, lido a cada requisição; o backend escreve os e-mails nele. */
  readonly readLocale: () => Locale
  /** Relógio em milissegundos desde a época, para interpretar Retry-After com data. */
  readonly now: () => number
  readonly sessionContext?: SessionContextStore
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
  const context = dependencies.sessionContext ?? createSessionContextStore()
  const purpose = (request: Request): 'public' | 'private' => {
    const path = new URL(request.url).pathname
    const method = request.method.toUpperCase()
    if (method === 'GET' && path === '/api/v1/me') throw contextAbort()
    if (
      ((method === 'GET' || method === 'HEAD') && path === '/api/v1/auth/csrf') ||
      isPublicAuthenticationMutation(method, path)
    )
      return 'public'
    if (isLogoutRequest(method, path)) {
      const state = context.getSnapshot()
      if (state.owner === null && state.pendingOwner === null && !state.protected) return 'public'
    }
    if (!path.startsWith('/api/v1/')) throw contextAbort()
    return 'private'
  }
  const send = async (request: Request, operation: OperationHandle): Promise<Response> => {
    assertSameOrigin(request.url, dependencies.origin)
    const scope = purpose(request)
    const policy = sessionHeaders(request, dependencies, scope === 'public')
    const outgoing =
      scope === 'private'
        ? bindPrivateRequest(context, operation, { request, headers: policy })
        : bindPublicRequest(context, operation, { request, headers: policy })
    const response = await fetchOrThrowNetworkError(dependencies.fetch, outgoing)
    context.assertCurrent(operation)
    if (response.status === 0) throw new ApiResponseError(0, 'resposta indisponível')
    const owned = new OwnedResponse(response, {
      assertCurrent: () => {
        context.assertCurrent(operation)
      },
      signal: outgoing.signal,
    })
    if (!owned.ok) {
      const error = await toResponseError(owned, dependencies.now())
      context.assertCurrent(operation)
      if (scope === 'private' && error instanceof ApiProblemError && error.problem.code === 'CSRF_VALIDATION_FAILED') {
        context.protect('context')
        throw contextAbort()
      }
      throw error
    }
    return owned
  }

  const toRequest = ({ method, path, body, headers = {}, signal }: JsonRequest): Request =>
    new Request(resolveSameOriginUrl(path, dependencies.origin), {
      method,
      headers: body === undefined ? headers : { ...headers, 'Content-Type': 'application/json' },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      ...(signal === undefined ? {} : { signal }),
    })

  const execute = async <T>(request: JsonRequest, consume: (response: Response) => Promise<T>): Promise<T> => {
    const outgoing = toRequest(request)
    assertSameOrigin(outgoing.url, dependencies.origin)
    const scope = purpose(outgoing)
    if (request.operation === undefined && scope === 'private') throw contextAbort()
    const operation = request.operation ?? context.beginPublic()
    try {
      const response = await send(outgoing, operation)
      const value = await consume(response)
      context.assertCurrent(operation)
      return value
    } finally {
      if (request.operation === undefined) context.finish(operation)
    }
  }

  return {
    sessionContext: context,
    fetch: (request) => {
      assertSameOrigin(request.url, dependencies.origin)
      return Promise.reject(contextAbort())
    },
    fetchFor: (operation) => (request) => send(request, operation),
    requestJson: (request) => execute(request, readJsonBody),
    sendCommand: (request) =>
      execute(request, async (response) => {
        await response.body?.cancel()
      }),
    async readMeBootstrap(ticket, signal) {
      context.assertBootstrap(ticket)
      const request = toRequest({ method: 'GET', path: '/api/v1/me', ...(signal === undefined ? {} : { signal }) })
      const outgoing = bindBootstrapRequest(context, ticket, {
        request,
        headers: sessionHeaders(request, dependencies, true),
      })
      const response = await fetchOrThrowNetworkError(dependencies.fetch, outgoing)
      context.assertBootstrap(ticket)
      if (response.status === 0) throw new ApiResponseError(0, 'resposta indisponível')
      const owned = new OwnedResponse(response, {
        assertCurrent: () => {
          context.assertBootstrap(ticket)
        },
        signal: outgoing.signal,
      })
      if (!owned.ok && owned.status !== 403) {
        const error = await toResponseError(owned, dependencies.now())
        context.assertBootstrap(ticket)
        throw error
      }
      if (!['application/json', 'application/problem+json'].includes(mediaTypeOf(owned))) {
        throw new ApiResponseError(owned.status, 'bootstrap sem corpo JSON')
      }
      let body: unknown
      try {
        body = await owned.json()
      } catch (error) {
        context.assertBootstrap(ticket)
        if (error instanceof DOMException && error.name === 'AbortError') throw error
        throw new ApiResponseError(owned.status, 'JSON de bootstrap inválido')
      }
      context.assertBootstrap(ticket)
      return { status: owned.status, body }
    },
  }
}

function sessionHeaders(request: Request, dependencies: ApiClientDependencies, isPublic: boolean): Headers {
  const headers = new Headers(request.headers)
  // O navegador mandaria a preferência dele, não a escolhida no seletor de idioma.
  headers.set('Accept-Language', dependencies.readLocale())
  headers.delete(ANTIFORGERY_HEADER_NAME)
  if (isPublic && !SAFE_METHODS.has(request.method.toUpperCase())) {
    const token = dependencies.readAntiforgeryToken()
    headers.delete(ANTIFORGERY_HEADER_NAME)
    if (token !== null) {
      headers.set(ANTIFORGERY_HEADER_NAME, token)
    }
  }
  return headers
}

async function fetchOrThrowNetworkError(fetch: ApiFetch, request: Request): Promise<Response> {
  try {
    return await fetch(request)
  } catch (error) {
    if (request.signal.aborted) {
      throw contextAbort()
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
  try {
    return (await response.json()) as unknown
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiResponseError(response.status, 'JSON inválido na resposta')
  }
}

async function readJsonOrNull(response: Response): Promise<unknown> {
  try {
    const body: unknown = await response.json()
    return body
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
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
