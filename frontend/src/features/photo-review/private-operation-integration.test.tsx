import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS } from '@/test/render'
import { server } from '@/test/msw'
import { TEST_PRIVATE_PROOF, withSessionContext } from '@/test/private-context'
import { apiClient } from '@/shared/api'

const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const OWNER = {
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  email: 'owner@example.test',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
  preferredLocale: 'en-US' as const,
}

describe('M7 against the composed private operation boundary', () => {
  it('fails closed when a Me fixture omits the real session context', async () => {
    let reads = 0
    server.use(
      http.get('/api/v1/me', () => HttpResponse.json(OWNER)),
      http.get('/api/v1/me/photos', () => {
        reads++
        return HttpResponse.json({ items: [], nextCursor: null })
      }),
    )
    await renderApp('/account/photos', undefined, 'en-US')
    await waitFor(() => {
      expect(apiClient.sessionContext.getSnapshot().protected).toBe(true)
    })
    expect(reads).toBe(0)
    expect(screen.queryByRole('link', { name: `Photo review history for ${PHOTO}` })).not.toBeInTheDocument()
  })

  it('bootstraps the real owner route and reads photo IDs through the existing private proof', async () => {
    const proofs: (string | null)[] = []
    server.use(
      http.get('/api/v1/me', () => HttpResponse.json(withSessionContext(OWNER))),
      http.get('/api/v1/me/photos', ({ request }) => {
        proofs.push(request.headers.get('X-XSRF-TOKEN'))
        return HttpResponse.json({
          items: [
            {
              photoId: PHOTO,
              status: 'Rejected',
              rejectionCode: 'PHOTO_REVIEW_REJECTED',
              createdAt: '2026-10-25T10:00:00+00:00',
            },
          ],
          nextCursor: null,
        })
      }),
    )
    await renderApp('/account/photos', undefined, 'en-US')
    expect(
      await screen.findByRole(
        'link',
        { name: `Photo review history for ${PHOTO}` },
        { timeout: ROUTE_CHUNK_TIMEOUT_MS },
      ),
    ).toHaveAttribute('href', `/photos/${PHOTO}/review`)
    expect(proofs).toEqual([TEST_PRIVATE_PROOF])
  })
})
