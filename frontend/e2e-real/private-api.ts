import { expect, type Page } from '@playwright/test'
import type { components } from '../src/shared/api/schema.d.ts'
import { ANTIFORGERY_HEADER_NAME } from '../src/shared/session-context.ts'

interface RequestOptions {
  method?: 'GET' | 'PUT' | 'POST'
  readBody?: boolean
}

export async function requestPrivateApi(
  page: Page,
  path: string,
  { method = 'GET', readBody = false }: RequestOptions = {},
): Promise<{ status: number; body: string }> {
  return page.evaluate(
    async ({ endpoint, verb, includeBody, proofHeader }) => {
      const sameOrigin: RequestInit = { mode: 'same-origin', credentials: 'same-origin', redirect: 'error' }
      const bootstrap = await fetch('/api/v1/me', { ...sameOrigin, cache: 'no-store' })
      if (bootstrap.status !== 200) throw new Error(`Session bootstrap returned HTTP ${String(bootstrap.status)}`)
      const { sessionContext } = (await bootstrap.json()) as Partial<
        Pick<components['schemas']['MeResponse'], 'sessionContext'>
      >
      if (sessionContext?.phase !== 'active' || !sessionContext.requestToken) {
        throw new Error('Expected an active session with private request proof')
      }

      const response = await fetch(endpoint, {
        ...sameOrigin,
        method: verb,
        headers: { [proofHeader]: sessionContext.requestToken },
      })
      if (includeBody && response.ok) return { status: response.status, body: await response.text() }
      await response.body?.cancel()
      return { status: response.status, body: '' }
    },
    { endpoint: path, verb: method, includeBody: readBody, proofHeader: ANTIFORGERY_HEADER_NAME },
  )
}

export async function readPrivateJson<T>(page: Page, path: string): Promise<T> {
  const { status, body } = await requestPrivateApi(page, path, { readBody: true })
  expect(status).toBe(200)
  return JSON.parse(body) as T
}

export async function enroll(page: Page, eventId: string): Promise<void> {
  const registration = await requestPrivateApi(page, `/api/v1/events/${eventId}/registration`, { method: 'PUT' })
  expect(registration.status).toBe(204)
  const checkIn = await requestPrivateApi(page, `/api/v1/events/${eventId}/check-in`, { method: 'POST' })
  expect(checkIn.status).toBe(204)
}
