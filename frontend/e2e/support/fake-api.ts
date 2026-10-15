import { randomUUID } from 'node:crypto'
import type { Page, Route } from '@playwright/test'

/**
 * Backend simulado do contrato de autenticação do M1, por `page.route`, com estado em memória por
 * teste. Confere o header antiforgery em toda mutação, como o servidor real, e guarda os "e-mails"
 * enviados para o teste seguir os links.
 */

const API_PREFIX = '/api/v1'
const ANTIFORGERY_COOKIE = 'XSRF-TOKEN'
const ANTIFORGERY_HEADER = 'x-xsrf-token'

interface FakeUser {
  readonly id: string
  readonly email: string
  readonly password: string
  emailConfirmed: boolean
  readonly confirmationToken: string
}

interface FakeSession {
  readonly id: string
  readonly userId: string
  readonly createdAt: string
  readonly description: string
}

export interface EmailLink {
  readonly path: string
  readonly userId: string
  readonly token: string
}

interface FailureTarget {
  readonly method: string
  readonly path: string
  readonly times?: number
}

type Override = 'network-error' | { readonly status: number; readonly code: string; readonly retryAfter?: string }

export class FakeDuoraApi {
  readonly #users = new Map<string, FakeUser>()
  readonly #sessions: FakeSession[] = []
  readonly #overrides = new Map<string, { readonly override: Override; remaining: number }>()
  readonly #confirmationLinks: EmailLink[] = []
  #currentSessionId: string | null = null
  #antiforgeryToken: string | null = null

  readonly #page: Page

  private constructor(page: Page) {
    this.#page = page
  }

  static async install(page: Page): Promise<FakeDuoraApi> {
    const api = new FakeDuoraApi(page)
    await page.route(`**${API_PREFIX}/**`, (route) => api.#handle(route))
    return api
  }

  /** Cria uma conta já confirmada, como se tivesse passado pelo cadastro. */
  seedConfirmedUser(email: string, password: string): void {
    this.#users.set(email, { id: randomUUID(), email, password, emailConfirmed: true, confirmationToken: randomUUID() })
  }

  /**
   * Faz as próximas `times` chamadas a `METHOD /caminho` responderem com erro ou falha de rede.
   * Leituras repetem falhas transitórias (até duas vezes), então um erro persistente pede `times` 3.
   */
  failNext({ method, path, times = 1 }: FailureTarget, override: Override): void {
    this.#overrides.set(`${method} ${path}`, { override, remaining: times })
  }

  lastConfirmationLink(): EmailLink {
    const link = this.#confirmationLinks.at(-1)
    if (link === undefined) {
      throw new Error('Nenhum e-mail de confirmação foi enviado.')
    }
    return link
  }

  async #handle(route: Route): Promise<void> {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.slice(API_PREFIX.length)
    const key = `${request.method()} ${path.replace(/^\/me\/sessions\/.+$/, '/me/sessions/:id')}`

    const failure = this.#overrides.get(key)
    if (failure !== undefined) {
      failure.remaining -= 1
      if (failure.remaining === 0) {
        this.#overrides.delete(key)
      }
      const { override } = failure
      if (override === 'network-error') {
        await route.abort('internetdisconnected')
        return
      }
      await route.fulfill(problem(override.status, override.code, override.retryAfter))
      return
    }

    if (request.method() === 'GET' && path === '/auth/csrf') {
      await this.#issueAntiforgeryToken(url.origin)
      await route.fulfill({ status: 204 })
      return
    }
    if (
      !['GET', 'HEAD'].includes(request.method()) &&
      request.headers()[ANTIFORGERY_HEADER] !== this.#antiforgeryToken
    ) {
      await route.fulfill(problem(400, 'ANTIFORGERY_TOKEN_INVALID'))
      return
    }
    const body: unknown = request.postDataJSON()
    await route.fulfill(this.#respond(key, body, path))
  }

  async #issueAntiforgeryToken(origin: string): Promise<void> {
    this.#antiforgeryToken = randomUUID()
    await this.#page.context().addCookies([{ name: ANTIFORGERY_COOKIE, value: this.#antiforgeryToken, url: origin }])
  }

  #respond(key: string, body: unknown, path: string): Fulfillment {
    switch (key) {
      case 'POST /auth/register':
        return this.#register(readRecord(body))
      case 'POST /auth/confirm-email':
        return this.#confirmEmail(readRecord(body))
      case 'POST /auth/login':
        return this.#login(readRecord(body))
      case 'POST /auth/logout':
        this.#currentSessionId = null
        return { status: 204 }
      case 'POST /auth/password-reset-requests':
        return { status: 202 }
      case 'POST /auth/password-resets':
        return problem(400, 'INVALID_TOKEN')
      case 'GET /me':
        return this.#me()
      case 'GET /me/sessions':
        return this.#listSessions()
      case 'DELETE /me/sessions/:id':
        return this.#revokeSession(decodeURIComponent(path.slice('/me/sessions/'.length)))
      default:
        return problem(404, 'NOT_FOUND')
    }
  }

  #register(body: Readonly<Record<string, unknown>>): Fulfillment {
    const email = String(body.email)
    if (!this.#users.has(email)) {
      const user: FakeUser = {
        id: randomUUID(),
        email,
        password: String(body.password),
        emailConfirmed: false,
        confirmationToken: randomUUID(),
      }
      this.#users.set(email, user)
      this.#confirmationLinks.push({
        path: `/confirmar-email?userId=${user.id}&token=${encodeURIComponent(user.confirmationToken)}`,
        userId: user.id,
        token: user.confirmationToken,
      })
    }
    return { status: 202 }
  }

  #confirmEmail(body: Readonly<Record<string, unknown>>): Fulfillment {
    const user = [...this.#users.values()].find((candidate) => candidate.id === body.userId)
    if (user === undefined || user.confirmationToken !== body.token) {
      return problem(400, 'INVALID_TOKEN')
    }
    user.emailConfirmed = true
    return { status: 204 }
  }

  #login(body: Readonly<Record<string, unknown>>): Fulfillment {
    const user = this.#users.get(String(body.email))
    if (user === undefined || user.password !== body.password) {
      return problem(401, 'INVALID_CREDENTIALS')
    }
    const session: FakeSession = {
      id: randomUUID(),
      userId: user.id,
      createdAt: new Date().toISOString(),
      description: `Navegador de teste (${user.email.split('@')[0] ?? ''})`,
    }
    this.#sessions.push(session)
    this.#currentSessionId = session.id
    return { status: 204 }
  }

  #currentUser(): FakeUser | null {
    const session = this.#sessions.find((candidate) => candidate.id === this.#currentSessionId)
    return [...this.#users.values()].find((user) => user.id === session?.userId) ?? null
  }

  #me(): Fulfillment {
    const user = this.#currentUser()
    if (user === null) {
      return problem(401, 'UNAUTHENTICATED')
    }
    return json({
      id: user.id,
      email: user.email,
      status: user.emailConfirmed ? 'Active' : 'PendingEmail',
      emailConfirmed: user.emailConfirmed,
    })
  }

  #listSessions(): Fulfillment {
    const user = this.#currentUser()
    if (user === null) {
      return problem(401, 'UNAUTHENTICATED')
    }
    const items = this.#sessions
      .filter((session) => session.userId === user.id)
      .map((session) => ({
        id: session.id,
        createdAt: session.createdAt,
        lastSeenAt: session.createdAt,
        current: session.id === this.#currentSessionId,
        description: session.description,
      }))
    return json({ items })
  }

  #revokeSession(sessionId: string): Fulfillment {
    const user = this.#currentUser()
    const index = this.#sessions.findIndex((session) => session.id === sessionId && session.userId === user?.id)
    if (user === null || index === -1) {
      return problem(404, 'NOT_FOUND')
    }
    this.#sessions.splice(index, 1)
    if (sessionId === this.#currentSessionId) {
      this.#currentSessionId = null
    }
    return { status: 204 }
  }
}

interface Fulfillment {
  readonly status: number
  readonly contentType?: string
  readonly body?: string
  readonly headers?: Record<string, string>
}

function json(body: unknown): Fulfillment {
  return { status: 200, contentType: 'application/json', body: JSON.stringify(body) }
}

function problem(status: number, code: string, retryAfter?: string): Fulfillment {
  return {
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title: code, status, code }),
    ...(retryAfter === undefined ? {} : { headers: { 'Retry-After': retryAfter } }),
  }
}

function readRecord(body: unknown): Readonly<Record<string, unknown>> {
  return typeof body === 'object' && body !== null ? Object.fromEntries(Object.entries(body)) : {}
}
