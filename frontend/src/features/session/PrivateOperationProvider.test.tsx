import { usePrivateOperation } from './private-operation'
import { acknowledgeOwnerInstallation } from './owner-installation'
import { QueryClient } from '@tanstack/react-query'
import { act, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createSessionContextStore, type SessionContextStore, type ValidatedBootstrap } from '@/shared/session-context'
import { PendingOwnerInstallation, PrivateOperationProvider } from './PrivateOperationProvider'

const BINDING: ValidatedBootstrap = {
  userId: '00000000-0000-4000-8000-000000000001',
  sessionId: '00000000-0000-4000-8000-000000000011',
  phase: 'active',
  requestToken: 'proof-A',
  authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] },
}
function pending(store: SessionContextStore, sid = BINDING.sessionId) {
  return store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), { ...BINDING, sessionId: sid })
}
function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

describe('barreira de instalação do dono privado', () => {
  it('commita ramo neutro antes de apagar caches e liberar o novo dono', async () => {
    const store = createSessionContextStore()
    const cache = new QueryClient()
    const owner = pending(store)
    const captured = { epoch: store.getSnapshot().transitionEpoch, owner }
    cache.setQueryData(['account', 'same-user', 'profile'], { from: 'A' })
    cache.setQueryData(['me'], { owner })
    const cancellation = deferred()
    const cancel = vi.spyOn(cache, 'cancelQueries').mockReturnValue(cancellation.promise)
    render(
      <PendingOwnerInstallation store={store} queryClient={cache} installation={captured}>
        <p>neutral</p>
      </PendingOwnerInstallation>,
    )
    expect(screen.getByText('neutral')).toBeInTheDocument()
    expect(store.getSnapshot().owner).toBeNull()
    expect(cache.getQueryData(['account', 'same-user', 'profile'])).toEqual({ from: 'A' })
    expect(cancel).toHaveBeenCalledOnce()
    await act(async () => {
      cancellation.resolve()
      await cancellation.promise
    })
    await waitFor(() => {
      expect(store.getSnapshot().owner).toBe(owner)
    })
    expect(cache.getQueryData(['account', 'same-user', 'profile'])).toBeUndefined()
    expect(cache.getQueryData(['me'])).toEqual({ owner })
    cache.clear()
  })

  it('ack B suspenso nunca limpa o cache de C nem ativa B', async () => {
    const store = createSessionContextStore()
    const cache = new QueryClient()
    const b = pending(store)
    const bEpoch = store.getSnapshot().transitionEpoch
    const cancellation = deferred()
    const originalCancel = cache.cancelQueries.bind(cache)
    vi.spyOn(cache, 'cancelQueries')
      .mockImplementationOnce(() => cancellation.promise)
      .mockImplementation(originalCancel)
    const earlier = acknowledgeOwnerInstallation(store, cache, { owner: b, epoch: bEpoch }).catch(
      (error: unknown) => error as Error,
    )
    const c = pending(store, '00000000-0000-4000-8000-000000000013')
    await acknowledgeOwnerInstallation(store, cache, { owner: c, epoch: store.getSnapshot().transitionEpoch })
    cache.setQueryData(['account', 'same-user', 'profile'], { from: 'C' })
    cancellation.resolve()
    expect(await earlier).toMatchObject({ name: 'AbortError' })
    expect(store.getSnapshot().owner).toBe(c)
    expect(cache.getQueryData(['account', 'same-user', 'profile'])).toEqual({ from: 'C' })
    cache.clear()
  })

  it('captura antes de validação assíncrona e não reaproveita uma intenção antiga', async () => {
    const store = createSessionContextStore()
    const owner = pending(store)
    store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
    const validation = deferred()
    const submitted = vi.fn()
    let submit!: () => Promise<void>
    function Probe() {
      const operation = usePrivateOperation()
      submit = () =>
        operation.run(async (snapshot) => {
          await validation.promise
          store.assertCurrent(snapshot)
          submitted()
        })
      return <p>private</p>
    }
    render(
      <PrivateOperationProvider owner={owner} store={store}>
        <Probe />
      </PrivateOperationProvider>,
    )
    const earlier = submit().catch((error: unknown) => error as Error)
    store.protect('context')
    validation.resolve()
    expect(await earlier).toMatchObject({ name: 'AbortError' })
    expect(submitted).not.toHaveBeenCalled()
  })
})
