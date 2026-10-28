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
import { PhotoReviewsPage } from './AdminPhotoReviewsPage'

const CASE = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const capabilities = { assign: true, evidence: true, decide: true, recentMfaRequiredForDecision: true }
const review = {
  caseId: CASE,
  state: 'AwaitingReview',
  eTag: '"case-v1"',
  awaitingReviewAt: '2026-10-25T10:00:00Z',
  reviewDeadline: '2026-10-28T10:00:00Z',
  policyVersion: 'policy-1',
}

function mount() {
  server.use(
    http.get('/api/v1/admin/photo-reviews/capabilities', () => HttpResponse.json(capabilities)),
    http.get(`/api/v1/admin/photo-reviews/${CASE}`, () =>
      HttpResponse.json(review, { headers: { ETag: review.eTag } }),
    ),
  )
  const me = installTestMe({
    id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    email: 'reviewer@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: true,
    roles: ['moderator'],
  })
  return renderWithI18n(
    <QueryClientProvider client={createTestQueryClient()}>
      <AccountContext value={me}>
        <PrivateOperationProvider owner={me.owner}>
          <MemoryRouter initialEntries={[`/admin/photo-reviews/${CASE}`]}>
            <Routes>
              <Route path="/admin/photo-reviews/:caseId" element={<PhotoReviewsPage />} />
            </Routes>
          </MemoryRouter>
        </PrivateOperationProvider>
      </AccountContext>
    </QueryClientProvider>,
    'en-US',
  )
}

function decoder() {
  const close = vi.fn()
  const bitmap: ImageBitmap = { width: 2, height: 3, close }
  const drawImage = vi.fn()
  const context = { drawImage }
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(((name: string) =>
    name === '2d' ? context : null) as unknown as HTMLCanvasElement['getContext'])
  const decode = vi.fn(() => Promise.resolve(bitmap))
  vi.stubGlobal('createImageBitmap', decode)
  const createUrl = vi.fn(() => 'blob:forbidden-render-path')
  vi.stubGlobal(
    'URL',
    class extends URL {
      static override createObjectURL = createUrl
      static override revokeObjectURL = vi.fn()
    },
  )
  return { bitmap, close, drawImage, decode, createUrl }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('private evidence rendering under the existing image policy', () => {
  it('keeps a transport failure visible without refetching or retaining image pixels', async () => {
    const native = decoder()
    const evidence = vi.fn(() => HttpResponse.error())
    server.use(http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/card`, evidence))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    expect(await screen.findByRole('alert')).toBeVisible()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(native.drawImage).not.toHaveBeenCalled()
    expect(evidence).toHaveBeenCalledOnce()
  })

  it('draws the authorized binary on an accessible canvas without a blob or data URL', async () => {
    const native = decoder()
    server.use(
      http.get(
        `/api/v1/admin/photo-reviews/${CASE}/evidence/card`,
        () => new HttpResponse(new Uint8Array([82, 73, 70, 70]), { headers: { 'Content-Type': 'image/webp' } }),
      ),
    )
    const view = mount()
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    const canvas = await screen.findByRole('img', { name: 'Private image for the assigned photo review' })
    expect(canvas.tagName).toBe('CANVAS')
    expect(canvas).not.toHaveAttribute('src')
    expect(native.drawImage).toHaveBeenCalledWith(native.bitmap, 0, 0)
    expect(native.close).toHaveBeenCalledOnce()
    expect(native.createUrl).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Close image' }))
    expect(canvas).not.toBeVisible()
    expect(canvas).toHaveAttribute('width', '0')
    expect(canvas).toHaveAttribute('height', '0')
    view.unmount()
  })

  it('keeps a sanitized 503 and Retry-After visible without another evidence request', async () => {
    decoder()
    const evidence = vi.fn(() =>
      HttpResponse.json(
        {
          title: 'Synthetic private failure',
          status: 503,
          code: 'PHOTO_REVIEW_UNAVAILABLE',
          detail: 'synthetic internal detail must remain hidden',
        },
        { status: 503, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '11' } },
      ),
    )
    server.use(http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/card`, evidence))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    expect(
      await screen.findByText('The service or evidence is unavailable. This does not approve or reject the photo.'),
    ).toBeVisible()
    expect(screen.getByText('Wait 11 seconds before trying again.')).toBeVisible()
    expect(screen.queryByText('synthetic internal detail must remain hidden')).not.toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(evidence).toHaveBeenCalledOnce()
  })

  it('closes a late decoded bitmap after unmount without drawing any pixels', async () => {
    const native = decoder()
    let release: ((bitmap: ImageBitmap) => void) | undefined
    const barrier = new Promise<ImageBitmap>((resolve) => {
      release = resolve
    })
    native.decode.mockImplementation(() => barrier)
    server.use(
      http.get(
        `/api/v1/admin/photo-reviews/${CASE}/evidence/card`,
        () => new HttpResponse(new Uint8Array([82, 73, 70, 70]), { headers: { 'Content-Type': 'image/webp' } }),
      ),
    )
    const view = mount()
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    await waitFor(() => {
      expect(native.decode).toHaveBeenCalledOnce()
    })
    view.unmount()
    release?.(native.bitmap)
    await waitFor(() => {
      expect(native.close).toHaveBeenCalledOnce()
    })
    expect(native.drawImage).not.toHaveBeenCalled()
  })

  it('reports an unsupported browser without fetching private image bytes', async () => {
    vi.stubGlobal('createImageBitmap', undefined)
    const evidence = vi.fn(() => new HttpResponse(null, { status: 503 }))
    server.use(http.get(`/api/v1/admin/photo-reviews/${CASE}/evidence/card`, evidence))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'View private image' }))
    expect(
      await screen.findByText(
        'This browser cannot display the private image securely. Use an up-to-date supported browser.',
      ),
    ).toBeVisible()
    expect(evidence).not.toHaveBeenCalled()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})
