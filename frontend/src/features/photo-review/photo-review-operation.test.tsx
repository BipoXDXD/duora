import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { apiClient } from '@/shared/api'
import { createApiClient } from '@/shared/api/api-client'
import { installTestMe, TEST_PRIVATE_PROOF, TEST_RENEWED_SESSION_ID, withSessionContext } from '@/test/private-context'
import { fetchMe } from '@/features/session/session-api'
import { renderWithI18n } from '@/test/render'
import { server } from '@/test/msw'
import { fetchReviewCapabilities, sendReviewIntent } from './photo-review-api'
import { useReviewCommand } from './review-command'
import { ReviewEvidence } from './ReviewEvidence'

const CASE = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const A = {
  id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  email: 'a@example.test',
  status: 'Active' as const,
  emailConfirmed: true,
  mfaEnabled: true,
  roles: ['moderator'],
}
const B = { ...A, id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', email: 'b@example.test' }
const CAPS = { assign: true, evidence: true, decide: true, appeal: true, recentMfaRequiredForDecision: true }
afterEach(() => {
  vi.unstubAllGlobals()
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

describe('M7 real private-operation consumer contract', () => {
  it('does not acquire a renewed session for the same user through an old operation', async () => {
    const me = installTestMe(A)
    const oldOperation = apiClient.sessionContext.begin(me.owner)
    const received = vi.fn(() => HttpResponse.json(CAPS))
    server.use(
      http.get('/api/v1/me', () => HttpResponse.json(withSessionContext(A, TEST_RENEWED_SESSION_ID))),
      http.get('/api/v1/admin/photo-reviews/capabilities', received),
    )
    const renewed = await fetchMe()
    const store = apiClient.sessionContext
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, renewed.owner)
    expect(renewed.id).toBe(me.id)
    expect(renewed.owner).not.toBe(me.owner)
    await expect(fetchReviewCapabilities(oldOperation)).rejects.toMatchObject({ name: 'AbortError' })
    expect(received).not.toHaveBeenCalled()
    store.finish(oldOperation)
  })

  it('uses the captured proof for a successful capability read rather than a public cookie', async () => {
    const me = installTestMe(A)
    const operation = apiClient.sessionContext.begin(me.owner)
    const proofs: (string | null)[] = []
    document.cookie = 'XSRF-TOKEN=public-B; Path=/'
    server.use(
      http.get('/api/v1/admin/photo-reviews/capabilities', ({ request }) => {
        proofs.push(request.headers.get('X-XSRF-TOKEN'))
        return HttpResponse.json(CAPS)
      }),
    )
    try {
      expect(await fetchReviewCapabilities(operation)).toEqual(CAPS)
    } finally {
      apiClient.sessionContext.finish(operation)
    }
    expect(proofs).toEqual([TEST_PRIVATE_PROOF])
  })

  it('refuses a capability read with no operation before HTTP', async () => {
    installTestMe(A)
    const received = vi.fn(() => HttpResponse.json(CAPS))
    server.use(http.get('/api/v1/admin/photo-reviews/capabilities', received))
    // @ts-expect-error Deliberately exercise a missing capability at the runtime boundary.
    await expect(fetchReviewCapabilities()).rejects.toMatchObject({ name: 'AbortError' })
    expect(received).not.toHaveBeenCalled()
  })

  it('does not move an old decision to B when identity changes before the first HTTP await', async () => {
    const me = installTestMe(A)
    const entered = deferred<undefined>()
    const release = deferred<undefined>()
    const writes = vi.fn(() => new HttpResponse(null, { status: 204 }))
    const confirmed = vi.fn()
    const failed = vi.fn()
    let completion: Promise<void> | undefined
    server.use(http.post(`/api/v1/admin/photo-reviews/${CASE}/decisions`, writes))
    function Intent() {
      const command = useReviewCommand({
        send: (operation, _value: string, signal) => {
          completion = (async () => {
            entered.resolve(undefined)
            await release.promise
            await sendReviewIntent(
              operation,
              {
                kind: 'review',
                id: CASE,
                eTag: '"case-A"',
                key: 'same-intent-A',
                command: { type: 'decision', decision: 'reject' },
              },
              signal,
            )
          })()
          return completion
        },
        onConfirmed: confirmed,
        onFailure: failed,
      })
      return (
        <button
          onClick={() => {
            command.submit('A')
          }}
        >
          send-A
        </button>
      )
    }
    renderWithI18n(
      <PrivateOperationProvider owner={me.owner}>
        <Intent />
      </PrivateOperationProvider>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'send-A' }))
    await entered.promise
    let nextOwner = me.owner
    await act(async () => {
      apiClient.sessionContext.protect('identity')
      nextOwner = installTestMe(B).owner
      release.resolve(undefined)
      await completion?.catch(() => undefined)
    })
    expect(writes).not.toHaveBeenCalled()
    expect(confirmed).not.toHaveBeenCalled()
    expect(failed).not.toHaveBeenCalled()
    expect(apiClient.sessionContext.getSnapshot().owner).toBe(nextOwner)
  })

  it('discards a JSON body suspended across an identity change without affecting B', async () => {
    const me = installTestMe(A)
    const operation = apiClient.sessionContext.begin(me.owner)
    const entered = deferred<undefined>()
    let controller!: ReadableStreamDefaultController<Uint8Array>
    let canceled = false
    const hasCanceled = (): boolean => canceled
    const body = new ReadableStream<Uint8Array>(
      {
        start(value) {
          controller = value
        },
        pull() {
          entered.resolve(undefined)
        },
        cancel() {
          canceled = true
        },
      },
      { highWaterMark: 0 },
    )
    // Use CF's real client/OwnedResponse with an isolated transport; do not replace the page fetch.
    const transport = createApiClient({
      sessionContext: apiClient.sessionContext,
      origin: location.origin,
      readAntiforgeryToken: () => null,
      readLocale: () => 'en-US',
      now: () => 0,
      fetch: () => Promise.resolve(new Response(body, { headers: { 'Content-Type': 'application/json' } })),
    })
    vi.spyOn(apiClient, 'fetchFor').mockImplementation((captured) => transport.fetchFor(captured))
    const pending = fetchReviewCapabilities(operation).catch((error: unknown) => error)
    await entered.promise
    apiClient.sessionContext.protect('identity')
    const next = installTestMe(B)
    if (!hasCanceled()) {
      controller.enqueue(new TextEncoder().encode(JSON.stringify(CAPS)))
      controller.close()
    }
    expect(await pending).toMatchObject({ name: 'AbortError' })
    expect(apiClient.sessionContext.getSnapshot().owner).toBe(next.owner)
    apiClient.sessionContext.finish(operation)
  })

  it('closes a decoded A bitmap after the operation owner changes and never draws under B', async () => {
    const me = installTestMe(A)
    const close = vi.fn()
    const bitmap: ImageBitmap = { width: 2, height: 2, close }
    const release = deferred<ImageBitmap>()
    const drawImage = vi.fn()
    const context = { drawImage }
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(((name: string) =>
      name === '2d' ? context : null) as unknown as HTMLCanvasElement['getContext'])
    const decode = vi.fn(() => release.promise)
    vi.stubGlobal('createImageBitmap', decode)
    server.use(
      http.get(
        `/api/v1/admin/photo-reviews/${CASE}/evidence/card`,
        () => new HttpResponse(new Uint8Array([82, 73, 70, 70]), { headers: { 'Content-Type': 'image/webp' } }),
      ),
    )
    renderWithI18n(
      <PrivateOperationProvider owner={me.owner}>
        <ReviewEvidence kind="review" itemId={CASE} onFailure={() => undefined} />
      </PrivateOperationProvider>,
      'en-US',
    )
    fireEvent.click(screen.getByRole('button', { name: 'View private image' }))
    await waitFor(() => {
      expect(decode).toHaveBeenCalledOnce()
    })
    await act(async () => {
      apiClient.sessionContext.protect('identity')
      installTestMe(B)
      release.resolve(bitmap)
      await release.promise
    })
    await waitFor(() => {
      expect(close).toHaveBeenCalledOnce()
    })
    expect(drawImage).not.toHaveBeenCalled()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    vi.unstubAllGlobals()
  })
})
