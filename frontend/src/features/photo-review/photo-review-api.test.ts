import { apiClient } from '@/shared/api'
import { installTestMe } from '@/test/private-context'
import { afterEach } from 'vitest'
import type { OperationHandle } from '@/shared/session-context'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '@/test/msw'
import {
  deleteOwnerPhoto,
  fetchOwnerPhotoVersion,
  fetchReviewHistory,
  fetchReviewItem,
  sendReviewIntent,
  uploadOwnerPhoto,
} from './photo-review-api'

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

describe('fronteira das fotos do titular (API-M01 a M03)', () => {
  const PHOTO_TAG = '"photo-bbbbbbbb-3"'
  const KEY = 'replay-1'
  const upload = {
    key: KEY,
    photo: { contentType: 'image/png', bytes: new Uint8Array([9, 8, 7]).buffer },
  } as const

  it('envia o corpo cru com tipo e chave, sem JSON, e devolve a foto aceita', async () => {
    let seen: { type: string | null; key: string | null; bytes: number[] } | null = null
    server.use(
      http.post('/api/v1/me/photos', async ({ request }) => {
        seen = {
          type: request.headers.get('Content-Type'),
          key: request.headers.get('Idempotency-Key'),
          bytes: [...new Uint8Array(await request.arrayBuffer())],
        }
        return HttpResponse.json({ photoId: PHOTO, status: 'Quarantined' }, { status: 202 })
      }),
    )

    await expect(uploadOwnerPhoto(operation(), upload)).resolves.toEqual({ photoId: PHOTO, status: 'Quarantined' })
    expect(seen).toEqual({ type: 'image/png', key: KEY, bytes: [9, 8, 7] })
  })

  it('não aceita recibo de envio fora do contrato', async () => {
    server.use(http.post('/api/v1/me/photos', () => HttpResponse.json({ photoId: 'x', status: 1 }, { status: 202 })))

    await expect(uploadOwnerPhoto(operation(), upload)).rejects.toThrow()
  })

  it('lê status e ETag da foto em API-M02', async () => {
    server.use(
      http.get(`/api/v1/me/photos/${PHOTO}/status`, () =>
        HttpResponse.json(
          { photoId: PHOTO, status: 'Rejected', rejectionCode: 'PHOTO_REVIEW_REJECTED' },
          { headers: { ETag: PHOTO_TAG } },
        ),
      ),
    )

    await expect(fetchOwnerPhotoVersion(operation(), PHOTO)).resolves.toEqual({
      photoId: PHOTO,
      status: 'Rejected',
      rejectionCode: 'PHOTO_REVIEW_REJECTED',
      eTag: PHOTO_TAG,
    })
  })

  it.each([
    ['ausente', {}],
    ['fraco', { ETag: 'W/"photo-1"' }],
    ['lista', { ETag: '"a", "b"' }],
    ['curinga', { ETag: '*' }],
  ])('recusa a leitura com ETag %s, que o DELETE não aceitaria', async (_name, headers: Record<string, string>) => {
    server.use(
      http.get(`/api/v1/me/photos/${PHOTO}/status`, () =>
        HttpResponse.json({ photoId: PHOTO, status: 'Approved', rejectionCode: null }, { headers }),
      ),
    )

    await expect(fetchOwnerPhotoVersion(operation(), PHOTO)).rejects.toThrow()
  })

  it('recusa status de outra foto mesmo com ETag válido', async () => {
    server.use(
      http.get(`/api/v1/me/photos/${PHOTO}/status`, () =>
        HttpResponse.json(
          { photoId: OTHER, status: 'Approved', rejectionCode: null },
          { headers: { ETag: PHOTO_TAG } },
        ),
      ),
    )

    await expect(fetchOwnerPhotoVersion(operation(), PHOTO)).rejects.toThrow()
  })

  it('exclui sem corpo, com If-Match e Idempotency-Key', async () => {
    let seen: { ifMatch: string | null; key: string | null; body: string } | null = null
    server.use(
      http.delete(`/api/v1/me/photos/${PHOTO}`, async ({ request }) => {
        seen = {
          ifMatch: request.headers.get('If-Match'),
          key: request.headers.get('Idempotency-Key'),
          body: await request.text(),
        }
        return new HttpResponse(null, { status: 204, headers: { ETag: '"photo-bbbbbbbb-4"' } })
      }),
    )

    await expect(deleteOwnerPhoto(operation(), { photoId: PHOTO, eTag: PHOTO_TAG, key: KEY })).resolves.toBeUndefined()
    expect(seen).toEqual({ ifMatch: PHOTO_TAG, key: KEY, body: '' })
  })
})
