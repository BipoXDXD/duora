import { apiClient, ApiNetworkError, classifyRequestFailure } from '@/shared/api'
import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { installTestMe } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AccountContext } from '@/features/session/account-context'
import { createTestQueryClient, renderWithI18n } from '@/test/render'
import { server } from '@/test/msw'
import { AdminPhotoReviewsPage } from './AdminPhotoReviewsPage'
import { PhotoReviewHistoryPage } from './PhotoReviewHistoryPage'
import { PhotoReviewHistoryLink } from './PhotoReviewHistoryLink'
import * as reviewApi from './photo-review-api'

const CASE = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const APPEAL = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const ACCOUNT = {
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  email: 'reviewer@example.test',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: true,
  roles: ['moderator'],
} as const
const capabilities = { assign: true, evidence: true, decide: true, appeal: true, recentMfaRequiredForDecision: true }
const review = {
  caseId: CASE,
  state: 'AwaitingReview',
  eTag: '"opaque-case-v1"',
  awaitingReviewAt: '2026-10-25T10:00:00Z',
  reviewDeadline: '2026-10-28T10:00:00Z',
  policyVersion: 'policy-1',
}
const appeal = {
  appealId: APPEAL,
  photoId: PHOTO,
  state: 'Open',
  eTag: '"opaque-appeal-v1"',
  requestedAt: '2026-10-25T11:00:00Z',
  responseTargetAt: '2026-11-01T11:00:00Z',
  maximumRetentionUntil: '2026-11-20T11:00:00Z',
  closedAt: null,
}
const history = {
  photoId: PHOTO,
  photoETag: '"opaque-photo-v1"',
  items: [
    {
      decisionId: CASE,
      decision: 'reject',
      reasonCode: 'does_not_meet_policy',
      decidedAt: '2026-10-25T10:00:00Z',
      isAppeal: false,
    },
  ],
  appeal: null,
  canRequestAppeal: true,
}

afterEach(() => {
  vi.unstubAllGlobals()
})

function problem(status: number, code = 'PHOTO_REVIEW_UNAVAILABLE') {
  return HttpResponse.json(
    { title: 'Synthetic problem', status, code },
    { status, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '5' } },
  )
}

function mount(path = '/admin/photo-reviews', locale: 'en-US' | 'pt-BR' = 'en-US', client = createTestQueryClient()) {
  const me = installTestMe({ ...ACCOUNT, roles: [...ACCOUNT.roles] })
  return renderWithI18n(
    <QueryClientProvider client={client}>
      <AccountContext value={me}>
        <PrivateOperationProvider owner={me.owner}>
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path="/admin/photo-reviews/:caseId?" element={<AdminPhotoReviewsPage kind="review" />} />
              <Route path="/admin/photo-appeals/:appealId?" element={<AdminPhotoReviewsPage kind="appeal" />} />
              <Route path="/photos/:photoId/review" element={<PhotoReviewHistoryPage />} />
            </Routes>
          </MemoryRouter>
        </PrivateOperationProvider>
      </AccountContext>
    </QueryClientProvider>,
    locale,
  )
}

function fixtures() {
  server.use(
    http.get(
      '/api/v1/auth/csrf',
      () => new HttpResponse(null, { status: 204, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic-csrf; Path=/' } }),
    ),
    http.get('/api/v1/admin/photo-reviews/capabilities', () => HttpResponse.json(capabilities)),
    http.get('/api/v1/admin/photo-reviews', () => HttpResponse.json({ items: [review], nextCursor: null })),
    http.get(`/api/v1/admin/photo-reviews/${CASE}`, () =>
      HttpResponse.json(review, { headers: { ETag: review.eTag } }),
    ),
    http.get('/api/v1/admin/photo-appeals', () => HttpResponse.json({ items: [appeal], nextCursor: null })),
    http.get(`/api/v1/admin/photo-appeals/${APPEAL}`, () =>
      HttpResponse.json(appeal, { headers: { ETag: appeal.eTag } }),
    ),
    http.get(`/api/v1/me/photos/${PHOTO}/review-history`, () =>
      HttpResponse.json(history, { headers: { ETag: history.photoETag } }),
    ),
  )
}

function nativeBitmap() {
  const bitmap = { width: 2, height: 2, close: vi.fn() }
  const context = { drawImage: vi.fn() }
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(((name: string) =>
    name === '2d' ? context : null) as unknown as HTMLCanvasElement['getContext'])
  vi.stubGlobal(
    'createImageBitmap',
    vi.fn(() => Promise.resolve(bitmap)),
  )
  return bitmap
}

describe('private photo review and appeal UI', () => {
  it('links the owner photo to its history without starting a review command', () => {
    renderWithI18n(
      <MemoryRouter>
        <PhotoReviewHistoryLink photoId={PHOTO} />
      </MemoryRouter>,
      'en-US',
    )
    expect(screen.getByRole('link', { name: 'Photo review history' })).toHaveAttribute(
      'href',
      `/photos/${PHOTO}/review`,
    )
  })
  it('does not fetch private metadata when a capability flag is missing', async () => {
    fixtures()
    const queue = vi.fn(() => HttpResponse.json({ items: [review], nextCursor: null }))
    server.use(
      http.get('/api/v1/admin/photo-reviews/capabilities', () =>
        HttpResponse.json({ assign: true, evidence: true, appeal: true, recentMfaRequiredForDecision: true }),
      ),
      http.get('/api/v1/admin/photo-reviews', queue),
    )
    mount()
    expect(await screen.findByRole('alert')).toBeVisible()
    expect(queue).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
  })

  it('cancels an in-flight image on exit without creating a late object URL', async () => {
    fixtures()
    nativeBitmap()
    let release: (() => void) | undefined
    const barrier = new Promise<void>((resolve) => {
      release = resolve
    })
    let signal: AbortSignal | undefined
    const create = vi.fn(() => 'blob:late-synthetic-photo')
    vi.stubGlobal(
      'URL',
      class extends URL {
        static override createObjectURL = create
        static override revokeObjectURL = vi.fn()
      },
    )
    server.use(
      http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/:variant`, async ({ request }) => {
        signal = request.signal
        await barrier
        return new HttpResponse(new Uint8Array([82, 73, 70, 70]), { headers: { 'Content-Type': 'image/webp' } })
      }),
    )
    const view = mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    await waitFor(() => {
      expect(signal).toBeDefined()
    })
    view.unmount()
    expect(signal?.aborted).toBe(true)
    release?.()
    await barrier
    expect(create).not.toHaveBeenCalled()
  })

  it('removes an open image and decision controls when authorization is revoked', async () => {
    fixtures()
    const bitmap = nativeBitmap()
    server.use(
      http.get(
        `/api/v1/admin/photo-reviews/${CASE}/evidence/:variant`,
        () => new HttpResponse(new Uint8Array([82, 73, 70, 70]), { headers: { 'Content-Type': 'image/webp' } }),
      ),
      http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, () => problem(403, 'FORBIDDEN')),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    const canvas = await screen.findByRole('img')
    expect(canvas).toBeVisible()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Decision' }), 'reject')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm decision' }))
    expect(await screen.findByText('Access is unavailable. Your permissions must be checked again.')).toBeVisible()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(bitmap.close).toHaveBeenCalledOnce()
    expect(canvas).toHaveAttribute('width', '0')
    expect(canvas).toHaveAttribute('height', '0')
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
  })

  it('sends assignment to its own endpoint without granting a local permission', async () => {
    fixtures()
    const received: { body: unknown; etag: string | null; key: string | null }[] = []
    server.use(
      http.post(`/api/v1/admin/photo-reviews/${CASE}/assignments`, async ({ request }) => {
        received.push({
          body: await request.json(),
          etag: request.headers.get('If-Match'),
          key: request.headers.get('Idempotency-Key'),
        })
        return HttpResponse.json({ caseId: CASE, eTag: '"case-v2"' })
      }),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.type(await screen.findByRole('textbox', { name: 'Reviewer identifier' }), ACCOUNT.id)
    await userEvent.click(screen.getByRole('button', { name: 'Confirm assignment' }))
    expect(await screen.findByText('Assignment confirmed by the server.')).toBeVisible()
    expect(received[0]?.body).toEqual({ reviewerId: ACCOUNT.id })
    expect(received[0]?.etag).toBe(review.eTag)
    expect(received[0]?.key).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
  })

  it('uses the appeal ETag and closed rejection reason on the appeal decision route', async () => {
    fixtures()
    const commands: { etag: string | null; body: unknown }[] = []
    server.use(
      http.post(`/api/v1/admin/photo-appeals/${APPEAL}/decisions`, async ({ request }) => {
        commands.push({ etag: request.headers.get('If-Match'), body: await request.json() })
        return HttpResponse.json({
          caseId: CASE,
          appealId: APPEAL,
          decision: 'reject',
          eTag: '"appeal-v2"',
          photoETag: '"photo-v2"',
          assetId: null,
        })
      }),
    )
    mount(`/admin/photo-appeals/${APPEAL}`)
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Decision' }), 'reject')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm decision' }))
    expect(await screen.findByText('Decision confirmed by the server.')).toBeVisible()
    expect(commands).toEqual([{ etag: appeal.eTag, body: { decision: 'reject', reasonCode: 'does_not_meet_policy' } }])
  })

  it('requires an explicit new decision after successful recent-TOTP verification', async () => {
    fixtures()
    const decisions = vi.fn(() => problem(401, 'REAUTHENTICATION_REQUIRED'))
    server.use(
      http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, decisions),
      http.post('/api/v1/auth/reauthenticate', () => new HttpResponse(null, { status: 204 })),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Decision' }), 'approve')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm decision' }))
    expect(
      await screen.findByText(
        'Confirm your identity again before continuing. No decision will be repeated automatically.',
      ),
    ).toBeVisible()
    await userEvent.type(screen.getByLabelText('Password'), 'synthetic phrase')
    await userEvent.type(screen.getByLabelText('Authenticator code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm identity with MFA' }))
    expect(await screen.findByRole('combobox', { name: 'Decision' })).toHaveValue('')
    expect(decisions).toHaveBeenCalledTimes(1)
  })

  it('revalidates capabilities when a detail read is forbidden', async () => {
    fixtures()
    let reads = 0
    server.use(
      http.get('/api/v1/admin/photo-reviews/capabilities', () => {
        reads++
        return HttpResponse.json(capabilities)
      }),
      http.get(`/api/v1/admin/photo-reviews/${CASE}`, () => problem(403, 'FORBIDDEN')),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    expect(await screen.findByText('Access is unavailable. Your permissions must be checked again.')).toBeVisible()
    await waitFor(() => {
      expect(reads).toBeGreaterThan(1)
    })
    expect(screen.queryByRole('button', { name: 'View private image' })).not.toBeInTheDocument()
  })

  it('cancels pending private reauthentication on unmount without a public CSRF round trip', async () => {
    fixtures()
    const authenticate = reviewApi.reauthenticateReviewer
    let completion: Promise<void> | undefined
    vi.spyOn(reviewApi, 'reauthenticateReviewer').mockImplementation((...args) => {
      completion = authenticate(...args)
      return completion
    })
    let release: (() => void) | undefined
    const barrier = new Promise<void>((resolve) => {
      release = resolve
    })
    let signal: AbortSignal | undefined
    const csrf = vi.fn(() => new HttpResponse(null, { status: 204 }))
    const reauth = vi.fn(async ({ request }: { request: Request }) => {
      signal = request.signal
      await barrier
      return new HttpResponse(null, { status: 204 })
    })
    server.use(http.get('/api/v1/auth/csrf', csrf), http.post('/api/v1/auth/reauthenticate', reauth))
    const view = mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.type(await screen.findByLabelText('Password'), 'synthetic phrase')
    await userEvent.type(screen.getByLabelText('Authenticator code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm identity with MFA' }))
    await waitFor(() => {
      expect(signal).toBeDefined()
    })
    view.unmount()
    release?.()
    await completion?.catch(() => undefined)
    expect(signal?.aborted).toBe(true)
    expect(reauth).toHaveBeenCalledOnce()
    expect(csrf).not.toHaveBeenCalled()
  })

  it('fails closed when capabilities are absent and never reads the queue or evidence', async () => {
    fixtures()
    const queue = vi.fn(() => HttpResponse.json({ items: [review], nextCursor: null }))
    server.use(
      http.get('/api/v1/admin/photo-reviews', queue),
      http.get('/api/v1/admin/photo-reviews/capabilities', () => problem(403, 'FORBIDDEN')),
    )
    mount()
    expect(await screen.findByRole('heading', { name: 'Photo reviews' })).toBeVisible()
    expect(await screen.findByText('Access is unavailable. Your permissions must be checked again.')).toBeVisible()
    expect(queue).not.toHaveBeenCalled()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
  })

  it('shows assigned metadata without fetching images and paginates with an opaque cursor', async () => {
    fixtures()
    const evidence = vi.fn(() => problem(403))
    const cursors: (string | null)[] = []
    server.use(
      http.get('/api/v1/admin/photo-reviews', ({ request }) => {
        const url = new URL(request.url)
        cursors.push(url.searchParams.get('cursor'))
        return HttpResponse.json({ items: [review], nextCursor: cursors.length === 1 ? 'opaque+/=' : null })
      }),
      http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/:variant`, evidence),
    )
    const client = createTestQueryClient()
    mount('/admin/photo-reviews', 'en-US', client)
    const link = await screen.findByRole(
      'link',
      { name: `Open case ${CASE}` },
      {
        // Passive failure-only projection: never serialize a query key, body, header or raw cause.
        onTimeout: (error) => {
          const context = apiClient.sessionContext.getSnapshot()
          const reads = client
            .getQueryCache()
            .getAll()
            .map(({ queryKey, state }) => ({
              phase: queryKey[3] === 'capabilities' ? 'capabilities' : 'queue',
              status: state.status,
              failure: state.status === 'error' ? classifyRequestFailure(state.error).kind : null,
              causeKind:
                state.error instanceof ApiNetworkError
                  ? state.error.cause instanceof TypeError
                    ? 'TypeError'
                    : state.error.cause instanceof DOMException
                      ? 'DOMException'
                      : state.error.cause instanceof Error
                        ? 'Error'
                        : 'non-error'
                  : null,
            }))
          const diagnostics = {
            queueHandlerCalls: cursors.length,
            contextProtectedAtTimeout: context.protected,
            ownerPresentAtTimeout: context.owner !== null,
            reads,
          }
          return new Error(`${error.message}\nM7_READ_DIAGNOSTICS ${JSON.stringify(diagnostics)}`)
        },
      },
    )
    expect(link).toHaveAttribute('href', `/admin/photo-reviews/${CASE}`)
    expect(evidence).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await waitFor(() => {
      expect(cursors).toEqual([null, 'opaque+/='])
    })
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('opens evidence only on request and clears its private canvas on exit', async () => {
    fixtures()
    const bitmap = nativeBitmap()
    const evidence = vi.fn(
      () =>
        new HttpResponse(new Uint8Array([82, 73, 70, 70]), {
          headers: { 'Content-Type': 'image/webp', 'Cache-Control': 'no-store' },
        }),
    )
    server.use(http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/:variant`, evidence))
    const view = mount(`/admin/photo-reviews/${CASE}`)
    const open = await screen.findByRole('button', { name: 'View private image' })
    expect(evidence).not.toHaveBeenCalled()
    await userEvent.click(open)
    const canvas = await screen.findByRole('img', { name: 'Private image for the assigned photo review' })
    expect(canvas.tagName).toBe('CANVAS')
    expect(canvas).not.toHaveAttribute('src')
    view.unmount()
    expect(bitmap.close).toHaveBeenCalledOnce()
    expect(canvas).toHaveAttribute('width', '0')
    expect(canvas).toHaveAttribute('height', '0')
    vi.unstubAllGlobals()
  })

  it('preserves the opaque ETag and reuses the same decision key only after explicit retry', async () => {
    fixtures()
    const commands: { etag: string | null; key: string | null; body: unknown }[] = []
    server.use(
      http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, async ({ request }) => {
        commands.push({
          etag: request.headers.get('If-Match'),
          key: request.headers.get('Idempotency-Key'),
          body: await request.json(),
        })
        return commands.length === 1
          ? problem(503)
          : HttpResponse.json({
              caseId: CASE,
              appealId: null,
              decision: 'approve',
              eTag: '"case-v2"',
              photoETag: '"photo-v2"',
              assetId: PHOTO,
            })
      }),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Decision' }), 'approve')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm decision' }))
    expect(await screen.findByRole('button', { name: 'Retry the same request' })).toBeVisible()
    expect(commands).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Retry the same request' }))
    expect(await screen.findByText('Decision confirmed by the server.')).toBeVisible()
    expect(commands).toHaveLength(2)
    expect(commands[0]?.etag).toBe(review.eTag)
    expect(commands[1]?.key).toBe(commands[0]?.key)
    expect(commands[0]?.key).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
    expect(commands[0]?.body).toEqual({ decision: 'approve', reasonCode: 'meets_policy' })
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('removes controls after a stale-version response and requires a fresh read', async () => {
    fixtures()
    let capabilityReads = 0
    server.use(
      http.get('/api/v1/admin/photo-reviews/capabilities', () => {
        capabilityReads++
        return HttpResponse.json(capabilities)
      }),
      http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, () => problem(412, 'PRECONDITION_FAILED')),
    )
    mount(`/admin/photo-reviews/${CASE}`)
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Decision' }), 'reject')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm decision' }))
    expect(
      await screen.findByText('This case changed. Read the current state before making another decision.'),
    ).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(capabilityReads).toBeGreaterThan(1)
    })
  })

  it('does not equate an unavailable appeal with approval or offer another decision', async () => {
    fixtures()
    server.use(
      http.get(`/api/v1/admin/photo-appeals/${APPEAL}`, () =>
        HttpResponse.json(
          { ...appeal, state: 'ClosedUnavailable', closedAt: '2026-10-25T12:00:00Z' },
          { headers: { ETag: appeal.eTag } },
        ),
      ),
    )
    mount(`/admin/photo-appeals/${APPEAL}`)
    expect(await screen.findByText('Appeal closed as unavailable, without a decision on the merits.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Confirm decision' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'View private image' })).not.toBeInTheDocument()
  })

  it('uses owner eligibility rather than administrative capabilities to request an appeal', async () => {
    fixtures()
    const admin = vi.fn(() => problem(403))
    let requested = false
    const sent: { etag: string | null; body: unknown }[] = []
    server.use(
      http.get('/api/v1/admin/photo-reviews/capabilities', admin),
      http.get(`/api/v1/me/photos/${PHOTO}/review-history`, () =>
        HttpResponse.json(
          { ...history, appeal: requested ? appeal : null, canRequestAppeal: !requested },
          { headers: { ETag: history.photoETag } },
        ),
      ),
      http.post(`/api/v1/me/photos/${PHOTO}/appeals`, async ({ request }) => {
        sent.push({ etag: request.headers.get('If-Match'), body: await request.json() })
        requested = true
        return HttpResponse.json(
          {
            appealId: APPEAL,
            photoId: PHOTO,
            photoETag: '"photo-v2"',
            requestedAt: appeal.requestedAt,
            responseTargetAt: appeal.responseTargetAt,
            maximumRetentionUntil: appeal.maximumRetentionUntil,
          },
          { status: 201 },
        )
      }),
    )
    mount(`/photos/${PHOTO}/review`)
    await userEvent.click(await screen.findByRole('button', { name: 'Request an appeal' }))
    expect(await screen.findByText('Appeal requested. The photo has not been approved.')).toBeVisible()
    expect(sent).toEqual([{ etag: history.photoETag, body: {} }])
    expect(admin).not.toHaveBeenCalled()
    expect(await screen.findByText('Open')).toBeVisible()
  })

  it('shows no appeal action for an ineligible owner and reveals no staff identity', async () => {
    fixtures()
    server.use(
      http.get(`/api/v1/me/photos/${PHOTO}/review-history`, () =>
        HttpResponse.json({ ...history, canRequestAppeal: false }, { headers: { ETag: history.photoETag } }),
      ),
    )
    mount(`/photos/${PHOTO}/review`, 'pt-BR')
    expect(await screen.findByRole('heading', { name: 'Histórico de revisão da foto' })).toBeVisible()
    expect(await screen.findByText('Não atende à política')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Pedir recurso' })).not.toBeInTheDocument()
    expect(screen.queryByText('reviewer@example.test')).not.toBeInTheDocument()
  })

  it('treats another owner and a missing photo identically without disclosing history', async () => {
    fixtures()
    server.use(http.get(`/api/v1/me/photos/${PHOTO}/review-history`, () => problem(404, 'NOT_FOUND')))
    mount(`/photos/${PHOTO}/review`)
    expect(await screen.findByText('This photo or case is unavailable.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Request an appeal' })).not.toBeInTheDocument()
    expect(screen.queryByText('Does not meet the policy')).not.toBeInTheDocument()
  })
})
