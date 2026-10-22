// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createApiClient } from './api-client'
import type { components } from './schema'

// There is no photo status screen yet. Exercise the actual shared transport without adding a product flow.
describe('photo status transport compatibility', () => {
  it.each([
    ['AwaitingReview', null],
    ['Rejected', 'PHOTO_REVIEW_REJECTED'],
    ['Rejected', 'PHOTO_REVIEW_EXPIRED'],
    ['Rejected', 'PHOTO_REVIEW_UNAVAILABLE'],
    ['FutureState', 'FUTURE_CODE'],
  ] as const)('preserves %s and %s without adopting or retrying the upload', async (status, rejectionCode) => {
    const payload: components['schemas']['PhotoStatusResponse'] = {
      photoId: '00000000-0000-4000-8000-000000000001',
      status,
      rejectionCode,
    }
    const fetch = vi.fn<(request: Request) => Promise<Response>>(() =>
      Promise.resolve(
        new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' },
        }),
      ),
    )
    const client = createApiClient({
      fetch,
      origin: 'https://duora.test',
      readAntiforgeryToken: () => null,
      readLocale: () => 'en-US',
      now: () => 0,
    })

    const result = await client.requestJson({ method: 'GET', path: `/api/v1/me/photos/${payload.photoId}/status` })

    expect(result).toEqual({
      photoId: '00000000-0000-4000-8000-000000000001',
      status,
      rejectionCode,
    })
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch.mock.calls[0]?.[0].method).toBe('GET')
  })
})
