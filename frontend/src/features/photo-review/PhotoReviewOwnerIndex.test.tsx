import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { installTestMe } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AccountContext } from '@/features/session/account-context'
import { createTestQueryClient, renderWithI18n } from '@/test/render'
import { server } from '@/test/msw'
import { PhotoReviewOwnerIndex } from './PhotoReviewOwnerIndex'

const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const OTHER = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
const photo = {
  photoId: PHOTO,
  status: 'Rejected',
  rejectionCode: 'PHOTO_REVIEW_REJECTED',
  createdAt: '2026-10-25T10:00:00+00:00',
}

function mount() {
  const me = installTestMe({
    id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    email: 'owner@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
  })
  return renderWithI18n(
    <QueryClientProvider client={createTestQueryClient()}>
      <AccountContext value={me}>
        <PrivateOperationProvider owner={me.owner}>
          <MemoryRouter>
            <PhotoReviewOwnerIndex />
          </MemoryRouter>
        </PrivateOperationProvider>
      </AccountContext>
    </QueryClientProvider>,
    'en-US',
  )
}

describe('owner photo index entry to review history', () => {
  it('uses returned owner photo IDs and an opaque cursor without requiring or inventing an ETag', async () => {
    const cursors: (string | null)[] = []
    const limits: (string | null)[] = []
    server.use(
      http.get('/api/v1/me/photos', ({ request }) => {
        const url = new URL(request.url)
        cursors.push(url.searchParams.get('cursor'))
        limits.push(url.searchParams.get('limit'))
        return HttpResponse.json(
          cursors.length === 1
            ? { items: [photo], nextCursor: 'opaque+/=' }
            : { items: [{ ...photo, photoId: OTHER }], nextCursor: null },
        )
      }),
    )
    mount()
    expect(await screen.findByRole('link', { name: `Photo review history for ${PHOTO}` })).toHaveAttribute(
      'href',
      `/photos/${PHOTO}/review`,
    )
    expect(screen.getByText('Does not meet the policy')).toBeVisible()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    expect(await screen.findByRole('link', { name: `Photo review history for ${OTHER}` })).toHaveAttribute(
      'href',
      `/photos/${OTHER}/review`,
    )
    expect(cursors).toEqual([null, 'opaque+/='])
    expect(limits).toEqual(['20', '20'])
    expect(screen.queryByRole('link', { name: `Photo review history for ${PHOTO}` })).not.toBeInTheDocument()
  })

  it('has an empty state rather than inventing a current photo or history link', async () => {
    server.use(http.get('/api/v1/me/photos', () => HttpResponse.json({ items: [], nextCursor: null })))
    mount()
    expect(await screen.findByText('No photos are available in this list.')).toBeVisible()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it.each([401, 403])(
    'fails closed on owner HTTP %s without consulting administrative capabilities',
    async (status) => {
      server.use(
        http.get('/api/v1/me/photos', () =>
          HttpResponse.json(
            { title: 'Synthetic denial', status },
            { status, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        ),
      )
      mount()
      expect(await screen.findByRole('alert')).toBeVisible()
      expect(screen.queryByRole('link')).not.toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Read current state' })).toBeVisible()
    },
  )

  it('does not keep the previous page visible after a denied next page', async () => {
    server.use(
      http.get('/api/v1/me/photos', ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? HttpResponse.json(
              { title: 'Synthetic denial', status: 403 },
              { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
            )
          : HttpResponse.json({ items: [photo], nextCursor: 'next' }),
      ),
    )
    mount()
    await screen.findByRole('link', { name: `Photo review history for ${PHOTO}` })
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await screen.findByRole('alert')
    await waitFor(() => {
      expect(screen.queryByRole('link')).not.toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: 'First page' })).toBeVisible()
  })

  it('does not render an unknown status or rejection detail as approval or raw server text', async () => {
    server.use(
      http.get('/api/v1/me/photos', () =>
        HttpResponse.json({
          items: [{ ...photo, status: 'future-internal-status', rejectionCode: 'internal-storage-detail' }],
          nextCursor: null,
        }),
      ),
    )
    mount()
    expect(await screen.findByText('Photo status is unavailable in this version. Read its history.')).toBeVisible()
    expect(screen.queryByText('future-internal-status')).not.toBeInTheDocument()
    expect(screen.queryByText('internal-storage-detail')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: `Photo review history for ${PHOTO}` })).toHaveAttribute(
      'href',
      `/photos/${PHOTO}/review`,
    )
  })
})
