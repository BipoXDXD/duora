import { act, renderHook } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { useFailureNotices } from './use-failure-notices'

type Source = 'command' | 'action'

function renderNotices(initialState: string) {
  return renderHook(({ state }: { state: string }) => useFailureNotices<Source>(state), {
    initialProps: { state: initialState },
  })
}

describe('useFailureNotices', () => {
  // O filho pode renderizar a falha antes de o pai receber a releitura: a falha vale para o estado relido, não para o
  // estado que o render ainda mostrava.
  it('keeps a failure current when the reread state reaches the render after the failure', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Expired')
    })
    notices.rerender({ state: 'Active' })

    notices.rerender({ state: 'Expired' })

    expect(notices.result.current.isCurrent('action')).toBe(true)
  })

  it('does not show a failure while the render still has the state before the reread', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Expired')
    })

    notices.rerender({ state: 'Active' })

    expect(notices.result.current.isCurrent('action')).toBe(false)
  })

  it('drops a failure on the first state change after it showed', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Expired')
    })
    notices.rerender({ state: 'Expired' })

    notices.rerender({ state: 'Completed' })

    expect(notices.result.current.isCurrent('action')).toBe(false)
  })

  it('keeps a dropped failure away when the room returns to its state', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Expired')
    })
    notices.rerender({ state: 'Expired' })
    notices.rerender({ state: 'Completed' })

    notices.rerender({ state: 'Expired' })

    expect(notices.result.current.isCurrent('action')).toBe(false)
  })

  it('treats a failure without a reread state as current', () => {
    const notices = renderNotices('Active')

    expect(notices.result.current.isCurrent('command')).toBe(true)
  })

  it('keeps the failures of each source apart', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Active')
    })
    notices.rerender({ state: 'Expired' })

    act(() => {
      notices.result.current.record('command', 'Expired')
    })

    expect(notices.result.current.isCurrent('command')).toBe(true)
    expect(notices.result.current.isCurrent('action')).toBe(false)
  })

  it('lets a new failure of a source replace the dropped one', () => {
    const notices = renderNotices('Active')
    act(() => {
      notices.result.current.record('action', 'Active')
    })
    notices.rerender({ state: 'Expired' })

    act(() => {
      notices.result.current.record('action', 'Expired')
    })

    expect(notices.result.current.isCurrent('action')).toBe(true)
  })
})
