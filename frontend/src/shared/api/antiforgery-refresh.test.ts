// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import type { ApiClient, JsonRequest } from './api-client'
import { ApiNetworkError } from './api-errors'
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

    expect(calls).toEqual([{ method: 'GET', path: '/api/v1/auth/csrf' }, mutation])
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
    const mutation: JsonRequest = { method: 'PATCH', path: '/api/v1/me/profile', body: {} }

    const body = await requestJsonWithFreshAntiforgeryToken({ ...client, requestJson }, mutation)

    expect(body).toEqual({ version: '2' })
    expect(calls).toEqual([{ method: 'GET', path: '/api/v1/auth/csrf' }, mutation])
  })
})
