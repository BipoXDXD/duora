// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createApiClient, type ApiClient, type JsonRequest } from './api-client'
import { ApiNetworkError } from './api-errors'
import { createSessionContextStore } from '../session-context'
import {
  refreshAntiforgeryToken,
  requestJsonWithFreshAntiforgeryToken,
  sendWithFreshAntiforgeryToken,
} from './antiforgery-refresh'

function fakeClient(sendCommand: (request: JsonRequest) => Promise<void> = () => Promise.resolve()) {
  const calls: JsonRequest[] = []
  const client: ApiClient = {
    requestJson: vi.fn(),
    fetch: vi.fn(),
    fetchFor: vi.fn(),
    readMeBootstrap: vi.fn(),
    sessionContext: createSessionContextStore(),
    sendCommand: (request) => {
      calls.push(request)
      return sendCommand(request)
    },
  }
  return { client, calls }
}

describe('refreshAntiforgeryToken', () => {
  it('pede um token novo em GET /api/v1/auth/csrf', async () => {
    const { client, calls } = fakeClient()

    await refreshAntiforgeryToken(client)

    expect(calls).toEqual([{ method: 'GET', path: '/api/v1/auth/csrf' }])
  })
})

describe('sendWithFreshAntiforgeryToken', () => {
  it('renova o token antes de enviar a mutação', async () => {
    const { client, calls } = fakeClient()
    const mutation: JsonRequest = { method: 'POST', path: '/api/v1/auth/login', body: { email: 'a@b.test' } }

    await sendWithFreshAntiforgeryToken(client, mutation)

    const operation = calls[0]?.operation
    expect(operation).toBeDefined()
    expect(calls).toEqual([
      { method: 'GET', path: '/api/v1/auth/csrf', operation },
      { ...mutation, operation },
    ])
  })

  it('não envia a mutação quando a renovação falha', async () => {
    const failure = new ApiNetworkError(new TypeError('offline'))
    const { client, calls } = fakeClient(() => Promise.reject(failure))

    await expect(sendWithFreshAntiforgeryToken(client, { method: 'POST', path: '/api/v1/auth/login' })).rejects.toBe(
      failure,
    )
    expect(calls).toHaveLength(1)
  })
})

describe('requestJsonWithFreshAntiforgeryToken', () => {
  it('renova o token antes e devolve o corpo da mutação', async () => {
    const { client, calls } = fakeClient()
    const requestJson = vi.fn((request: JsonRequest) => {
      calls.push(request)
      return Promise.resolve({ version: '2' })
    })
    const mutation: JsonRequest = { method: 'POST', path: '/api/v1/auth/register', body: {} }

    const body = await requestJsonWithFreshAntiforgeryToken({ ...client, requestJson }, mutation)

    expect(body).toEqual({ version: '2' })
    const operation = calls[0]?.operation
    expect(operation).toBeDefined()
    expect(calls).toEqual([
      { method: 'GET', path: '/api/v1/auth/csrf', operation },
      { ...mutation, operation },
    ])
  })
})

describe('a renovação pública não transporta intenção privada', () => {
  it.each(['command', 'json'] as const)(
    'rejeita %s privado antes de buscar CSRF sob outra identidade',
    async (kind) => {
      let token = 'public-proof-A'
      const writes: string[] = []
      const client = createApiClient({
        origin: 'https://duora.test',
        readAntiforgeryToken: () => token,
        readLocale: () => 'pt-BR',
        now: () => 0,
        fetch: async (request) => {
          if (new URL(request.url).pathname === '/api/v1/auth/csrf') {
            token = 'public-proof-B'
            return new Response(null, { status: 204 })
          }
          writes.push(`${request.headers.get('X-XSRF-TOKEN') ?? '<missing>'}:${await request.text()}`)
          return new Response(JSON.stringify({ version: '2' }), {
            headers: { 'Content-Type': 'application/json' },
          })
        },
      })
      const intent: JsonRequest = {
        method: 'PUT',
        path: '/api/v1/me/time-zone',
        body: { timeZone: 'America/Sao_Paulo' },
      }

      await (
        kind === 'command'
          ? sendWithFreshAntiforgeryToken(client, intent)
          : requestJsonWithFreshAntiforgeryToken(client, intent)
      ).catch(() => undefined)

      expect(writes).toEqual([])
      expect(token).toBe('public-proof-A')
    },
  )
})
