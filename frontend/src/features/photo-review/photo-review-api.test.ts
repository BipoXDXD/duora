import { apiClient } from '@/shared/api'
import { installTestMe } from '@/test/private-context'
import { afterEach } from 'vitest'
import type { OperationHandle } from '@/shared/session-context'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '@/test/msw'
import { fetchReviewHistory, fetchReviewItem, sendReviewIntent } from './photo-review-api'

const operations: OperationHandle[] = []
function operation(): OperationHandle {
  const me = installTestMe({
    id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    email: 'reviewer@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: true,
    roles: ['moderator'],
  })
  const next = apiClient.sessionContext.begin(me.owner)
  operations.push(next)
  return next
}
afterEach(() => {
  for (const item of operations.splice(0)) apiClient.sessionContext.finish(item)
})

const CASE = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const OTHER = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const ETag = '"opaque-/case-v1"'
const review = {
  caseId: CASE,
  state: 'AwaitingReview',
  eTag: ETag,
  awaitingReviewAt: '2026-10-25T10:00:00Z',
  reviewDeadline: '2026-10-28T10:00:00Z',
  policyVersion: 'policy-1',
}

describe('M7 localized HTTP boundary', () => {
  it('refuses a detail response for a different case even with a matching ETag', async () => {
    server.use(
      http.get(`/api/v1/admin/photo-reviews/${CASE}`, () =>
        HttpResponse.json({ ...review, caseId: OTHER }, { headers: { ETag } }),
      ),
    )
    await expect(fetchReviewItem(operation(), CASE)).rejects.toThrow()
  })

  it('refuses a different owner photo rather than exposing its factual history', async () => {
    server.use(
      http.get(`/api/v1/me/photos/${PHOTO}/review-history`, () =>
        HttpResponse.json({ photoId: OTHER, photoETag: ETag, items: [] }, { headers: { ETag } }),
      ),
    )
    await expect(fetchReviewHistory(operation(), PHOTO)).rejects.toThrow()
  })

  it('requires the header ETag to match the opaque DTO value exactly', async () => {
    server.use(
      http.get(`/api/v1/admin/photo-reviews/${CASE}`, () =>
        HttpResponse.json(review, { headers: { ETag: '"other-version"' } }),
      ),
    )
    await expect(fetchReviewItem(operation(), CASE)).rejects.toThrow()
  })

  it('does not acknowledge a different case decision as the requested decision', async () => {
    server.use(
      http.get('/api/v1/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, () =>
        HttpResponse.json({
          caseId: OTHER,
          decision: 'approve',
          eTag: '"case-v2"',
          photoETag: '"photo-v2"',
          assetId: PHOTO,
        }),
      ),
    )
    await expect(
      sendReviewIntent(operation(), {
        id: CASE,
        eTag: ETag,
        key: 'synthetic-request',
        command: { type: 'decision', decision: 'approve' },
      }),
    ).rejects.toThrow()
  })
})
