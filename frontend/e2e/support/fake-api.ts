import { syntheticLegalCatalog } from '../../src/test/legal-documents.js'
import { randomUUID } from 'node:crypto'
import type { Page, Route } from '@playwright/test'

/**
 * Backend simulado do contrato de autenticação do M1, por `page.route`, com estado em memória por
 * teste. Como o servidor real, confere o header antiforgery em toda mutação (403
 * CSRF_VALIDATION_FAILED) e recusa corpo fora do contrato (400 VALIDATION_FAILED): campo faltando,
 * campo desconhecido ou tipo errado fazem o teste falhar. Guarda os "e-mails" enviados para o teste
 * seguir os links.
 */

const API_PREFIX = '/api/v1'
const ANTIFORGERY_COOKIE = 'XSRF-TOKEN'
const ANTIFORGERY_HEADER = 'x-xsrf-token'

/** Campos exatos de cada corpo do contrato; rota fora daqui não aceita corpo. Texto, salvo os de BOOLEAN_FIELDS. */
const REQUEST_FIELDS: Readonly<Record<string, readonly string[]>> = {
  'POST /auth/register': [
    'email',
    'password',
    'acceptedTermsVersion',
    'acceptedPrivacyVersion',
    'acceptedTermsLocale',
    'acceptedPrivacyLocale',
    'acceptedTermsSha256',
    'acceptedPrivacySha256',
    'confirmedAdult',
  ],
  'POST /auth/confirm-email': ['userId', 'token', 'password'],
  'POST /auth/login': ['email', 'password'],
  'POST /auth/password-reset-requests': ['email'],
  'POST /auth/password-resets': ['userId', 'token', 'newPassword'],
}

/** Campos booleanos do contrato; chegam ao corpo simulado como 'true' ou 'false'. */
const BOOLEAN_FIELDS: ReadonlySet<string> = new Set(['confirmedAdult'])

type RequestBody = Readonly<Record<string, string>>

interface FakeUser {
  readonly id: string
  readonly email: string
  password: string
  emailConfirmed: boolean
  readonly confirmationToken: string
  passwordResetToken: string | null
}

interface FakeProfile {
  displayName: string | null
  bio: string | null
  regionCode: string | null
  countryCode?: string | null
  subdivisionCode?: string | null
  version: number
}

const PROFILE_FIELDS: readonly string[] = ['displayName', 'bio', 'regionCode', 'countryCode', 'subdivisionCode']

/** Formato da UF em ISO 3166-2:BR; a lista exata das 27 é do servidor e do contrato, não deste dublê. */
const REGION_CODE_FORMAT = /^BR-[A-Z]{2}$/

interface FakeSession {
  readonly id: string
  readonly userId: string
  readonly createdAt: string
  readonly description: string
}

type EmailKind = 'confirmation' | 'password-reset'

/** Rotas da SPA para onde os links de e-mail apontam, com userId e token no fragmento. */
const EMAIL_ROUTES: Readonly<Record<EmailKind, string>> = {
  confirmation: '/confirm-email',
  'password-reset': '/reset-password',
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
  readonly #emails: { readonly kind: EmailKind; readonly link: EmailLink }[] = []
  readonly #profiles = new Map<string, FakeProfile>()
  #currentSessionId: string | null = null
  #antiforgeryToken: string | null = null
  #publicProofSessionId: string | null = null
  readonly #privateProofs = new Map<string, string>()

  readonly #page: Page

  private constructor(page: Page) {
    this.#page = page
  }

  /** Instala no contexto do navegador: as abas abertas nele falam com o mesmo backend, como no servidor real. */
  static async install(page: Page): Promise<FakeDuoraApi> {
    const api = new FakeDuoraApi(page)
    await page.context().route(`**${API_PREFIX}/**`, (route) => api.#handle(route))
    return api
  }

  /** A sessão corrente expira no servidor (ociosa, absoluta ou revogada em outro lugar); o cookie continua lá. */
  expireCurrentSession(): void {
    this.#currentSessionId = null
  }

  /** Cria uma conta já confirmada, como se tivesse passado pelo cadastro. */
  seedConfirmedUser(email: string, password: string): void {
    this.#users.set(email, {
      id: randomUUID(),
      email,
      password,
      emailConfirmed: true,
      confirmationToken: randomUUID(),
      passwordResetToken: null,
    })
  }

  /**
   * Faz as próximas `times` chamadas a `METHOD /caminho` responderem com erro ou falha de rede.
   * Leituras repetem falhas transitórias (até duas vezes), então um erro persistente pede `times` 3.
   */
  failNext({ method, path, times = 1 }: FailureTarget, override: Override): void {
    this.#overrides.set(`${method} ${path}`, { override, remaining: times })
  }

  /** Outra aba ou aparelho grava o perfil da conta por fora da página em teste: a versão sobe. */
  changeProfileElsewhere(email: string, change: Partial<Omit<FakeProfile, 'version'>>): void {
    const user = this.#users.get(email)
    if (user === undefined) {
      throw new Error(`Conta ${email} não existe na API falsa.`)
    }
    const profile = this.#profileOf(user.id)
    Object.assign(profile, change)
    profile.version += 1
  }

  /** Explicit fixtures for current eight-digit codes and still-valid legacy tokens. */
  seedPendingUser(email: string, password: string, token: string): void {
    const user: FakeUser = {
      id: randomUUID(),
      email,
      password,
      emailConfirmed: false,
      confirmationToken: token,
      passwordResetToken: null,
    }
    this.#users.set(email, user)
    this.#sendEmail('confirmation', user.id, token)
  }

  /** Link do último e-mail do tipo pedido, como chegaria na caixa de entrada. */
  lastEmailLink(kind: EmailKind): EmailLink {
    const email = this.#emails.findLast((candidate) => candidate.kind === kind)
    if (email === undefined) {
      throw new Error(`Nenhum e-mail do tipo ${kind} foi enviado.`)
    }
    return email.link
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
    const publicMutation =
      new Set([
        'POST /auth/register',
        'POST /auth/confirm-email',
        'POST /auth/login',
        'POST /auth/password-reset-requests',
        'POST /auth/password-resets',
        'POST /auth/email-confirmation-requests',
      ]).has(key) ||
      (key === 'POST /auth/logout' && this.#currentSessionId === null)
    const bootstrap = key === 'GET /me' || key === 'GET /legal-documents'
    if (publicMutation) {
      if (
        request.headers()[ANTIFORGERY_HEADER] !== this.#antiforgeryToken ||
        this.#publicProofSessionId !== this.#currentSessionId
      ) {
        await route.fulfill(problem(403, 'CSRF_VALIDATION_FAILED'))
        return
      }
    } else if (!bootstrap) {
      if (this.#currentSessionId === null) {
        await route.fulfill(problem(401, 'UNAUTHENTICATED'))
        return
      }
      const expectedProof = this.#privateProofs.get(this.#currentSessionId)
      if (expectedProof === undefined || request.headers()[ANTIFORGERY_HEADER] !== expectedProof) {
        await route.fulfill(problem(403, 'CSRF_VALIDATION_FAILED'))
        return
      }
    }
    if (key === 'PATCH /me/profile') {
      await route.fulfill(this.#updateProfile(request.postData(), request.headers()['if-match'] ?? null))
      return
    }
    const body = parseBody(key, request.postData())
    if (body === null) {
      await route.fulfill(problem(400, 'VALIDATION_FAILED'))
      return
    }
    await route.fulfill(this.#respond(key, body, path))
  }

  async #issueAntiforgeryToken(origin: string): Promise<void> {
    this.#antiforgeryToken = randomUUID()
    this.#publicProofSessionId = this.#currentSessionId
    await this.#page.context().addCookies([{ name: ANTIFORGERY_COOKIE, value: this.#antiforgeryToken, url: origin }])
  }

  #respond(key: string, body: RequestBody, path: string): Fulfillment {
    switch (key) {
      case 'GET /legal-documents':
        return json(syntheticLegalCatalog)
      case 'POST /auth/register':
        return this.#register(body)
      case 'POST /auth/confirm-email':
        return this.#confirmEmail(body)
      case 'POST /auth/login':
        return this.#login(body)
      case 'POST /auth/logout':
        this.#currentSessionId = null
        return { status: 204 }
      case 'POST /auth/password-reset-requests':
        return this.#requestPasswordReset(body)
      case 'POST /auth/password-resets':
        return this.#resetPassword(body)
      case 'GET /me':
        return this.#me()
      case 'GET /me/sessions':
        return this.#listSessions()
      case 'GET /me/region-detection/capability':
        return this.#currentUser() === null ? problem(401, 'UNAUTHENTICATED') : json({ enabled: false })
      case 'GET /me/profile':
        return this.#readProfile()
      case 'DELETE /me/sessions/:id':
        return this.#revokeSession(decodeURIComponent(path.slice('/me/sessions/'.length)))
      default:
        return problem(404, 'NOT_FOUND')
    }
  }

  #register(body: RequestBody): Fulfillment {
    const matches = (document: 'terms' | 'privacy') =>
      syntheticLegalCatalog.documents.some(
        (candidate) =>
          candidate.document === document &&
          candidate.version === body[document === 'terms' ? 'acceptedTermsVersion' : 'acceptedPrivacyVersion'] &&
          candidate.locale === body[document === 'terms' ? 'acceptedTermsLocale' : 'acceptedPrivacyLocale'] &&
          candidate.sha256 === body[document === 'terms' ? 'acceptedTermsSha256' : 'acceptedPrivacySha256'],
      )
    if (!matches('terms') || !matches('privacy') || body.confirmedAdult !== 'true')
      return problem(400, 'VALIDATION_FAILED')
    const email = body.email ?? ''
    if (!this.#users.has(email)) {
      const user: FakeUser = {
        id: randomUUID(),
        email,
        password: body.password ?? '',
        emailConfirmed: false,
        confirmationToken: randomUUID(),
        passwordResetToken: null,
      }
      this.#users.set(email, user)
      this.#sendEmail('confirmation', user.id, user.confirmationToken)
    }
    return { status: 202 }
  }

  #sendEmail(kind: EmailKind, userId: string, token: string): void {
    const linkToken = kind === 'confirmation' && /^[0-9]{8}$/.test(token) ? 'code' : token
    const path = `${EMAIL_ROUTES[kind]}#userId=${userId}&token=${encodeURIComponent(linkToken)}`
    this.#emails.push({ kind, link: { path, userId, token } })
  }

  #requestPasswordReset(body: RequestBody): Fulfillment {
    const user = this.#users.get(body.email ?? '')
    if (user !== undefined) {
      user.passwordResetToken = randomUUID()
      this.#sendEmail('password-reset', user.id, user.passwordResetToken)
    }
    return { status: 202 }
  }

  #resetPassword(body: RequestBody): Fulfillment {
    const user = [...this.#users.values()].find((candidate) => candidate.id === body.userId)
    if (user?.passwordResetToken == null || user.passwordResetToken !== body.token) {
      return problem(400, 'INVALID_TOKEN')
    }
    user.password = body.newPassword ?? ''
    user.passwordResetToken = null
    // A troca de senha encerra as sessões anteriores (API-A07).
    const remaining = this.#sessions.filter((session) => session.userId !== user.id)
    this.#sessions.splice(0, this.#sessions.length, ...remaining)
    return { status: 204 }
  }

  #confirmEmail(body: RequestBody): Fulfillment {
    const user = [...this.#users.values()].find((candidate) => candidate.id === body.userId)
    // Senha errada responde igual a token inválido: o erro não revela qual dos dois falhou.
    if (user === undefined || user.confirmationToken !== body.token || user.password !== body.password) {
      return problem(400, 'INVALID_TOKEN')
    }
    user.emailConfirmed = true
    return { status: 204 }
  }

  #login(body: RequestBody): Fulfillment {
    const user = this.#users.get(body.email ?? '')
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
    const sessionId = this.#currentSessionId
    if (user === null || sessionId === null) {
      return problem(401, 'UNAUTHENTICATED')
    }
    let privateProof = this.#privateProofs.get(sessionId)
    if (privateProof === undefined) {
      privateProof = randomUUID()
      this.#privateProofs.set(sessionId, privateProof)
    }
    return json({
      id: user.id,
      email: user.email,
      status: user.emailConfirmed ? 'Active' : 'PendingEmail',
      emailConfirmed: user.emailConfirmed,
      mfaEnabled: false,
      roles: ['participant'],
      sessionContext: { userId: user.id, sessionId, phase: 'active', requestToken: privateProof },
    })
  }

  #profileOf(userId: string): FakeProfile {
    const existing = this.#profiles.get(userId)
    if (existing !== undefined) {
      return existing
    }
    const created: FakeProfile = { displayName: null, bio: null, regionCode: null, version: 0 }
    this.#profiles.set(userId, created)
    return created
  }

  #readProfile(): Fulfillment {
    const user = this.#currentUser()
    return user === null ? problem(401, 'UNAUTHENTICATED') : profileResponse(this.#profileOf(user.id))
  }

  /**
   * PATCH com a regra do servidor: If-Match obrigatório (428), versão desatualizada é 412 sem gravar, só os campos
   * do contrato, com texto ou null; displayName null é recusado e regionCode só aceita o formato canônico.
   */
  #updateProfile(raw: string | null, ifMatch: string | null): Fulfillment {
    const user = this.#currentUser()
    if (user === null) {
      return problem(401, 'UNAUTHENTICATED')
    }
    if (ifMatch === null || ifMatch === '*') {
      return problem(428, 'PRECONDITION_REQUIRED')
    }
    const body = parseJson(raw)
    if (typeof body !== 'object' || body === null || Array.isArray(body)) {
      return problem(400, 'VALIDATION_FAILED')
    }
    const entries = Object.entries(body)
    const valid =
      entries.every(
        ([name, value]) => PROFILE_FIELDS.includes(name) && (typeof value === 'string' || value === null),
      ) &&
      !entries.some(([name, value]) => name === 'displayName' && value === null) &&
      !entries.some(
        ([name, value]) => name === 'regionCode' && typeof value === 'string' && !REGION_CODE_FORMAT.test(value),
      )
    if (!valid) {
      return problem(400, 'VALIDATION_FAILED')
    }
    const profile = this.#profileOf(user.id)
    if (ifMatch !== `"${String(profile.version)}"`) {
      return problem(412, 'PRECONDITION_FAILED')
    }
    const changes: Partial<FakeProfile> = Object.fromEntries(entries)
    if (changes.countryCode !== undefined) {
      changes.subdivisionCode ??= null
      changes.regionCode = changes.countryCode === 'BR' ? changes.subdivisionCode : null
    } else if (changes.regionCode !== undefined) {
      changes.countryCode = changes.regionCode === null ? null : 'BR'
      changes.subdivisionCode = changes.regionCode
    }
    Object.assign(profile, changes)
    profile.version += 1
    return profileResponse(profile)
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
    if (user === null) {
      return problem(401, 'UNAUTHENTICATED')
    }
    const index = this.#sessions.findIndex((session) => session.id === sessionId && session.userId === user.id)
    if (index === -1) {
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

function profileResponse(profile: FakeProfile): Fulfillment {
  return {
    ...json({
      displayName: profile.displayName,
      bio: profile.bio,
      regionCode: profile.regionCode,
      countryCode: profile.countryCode,
      subdivisionCode: profile.subdivisionCode,
      version: String(profile.version),
    }),
    headers: { ETag: `"${String(profile.version)}"` },
  }
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

/**
 * Corpo da requisição contra o contrato: rotas com corpo exigem exatamente os campos listados, em texto
 * (ou booleano, para BOOLEAN_FIELDS); as demais não aceitam corpo. Devolve null quando o corpo não
 * respeita o contrato.
 */
function parseBody(key: string, raw: string | null): RequestBody | null {
  const fields = REQUEST_FIELDS[key]
  if (fields === undefined) {
    return raw === null || raw === '' ? {} : null
  }
  const body = parseJson(raw)
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return null
  }
  const entries = Object.entries(body)
  const exactFields = entries.length === fields.length && entries.every(([name]) => fields.includes(name))
  const wellTyped = entries.every(([name, value]) =>
    BOOLEAN_FIELDS.has(name) ? typeof value === 'boolean' : typeof value === 'string',
  )
  return exactFields && wellTyped ? Object.fromEntries(entries.map(([name, value]) => [name, String(value)])) : null
}

function parseJson(raw: string | null): unknown {
  if (raw === null) {
    return null
  }
  try {
    const parsed: unknown = JSON.parse(raw)
    return parsed
  } catch {
    // JSON malformado é corpo fora do contrato: quem chama responde 400.
    return null
  }
}
