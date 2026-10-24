import { useEffect, useLayoutEffect, useRef, type RefObject } from 'react'

/** Restore a removed game control's focus without interrupting someone reading elsewhere. */
export function useGameFocus(active: boolean): RefObject<HTMLElement | null> {
  const panel = useRef<HTMLElement>(null)
  const focused = useRef<HTMLElement | null>(null)
  useEffect(() => {
    const remember = (event: FocusEvent) => {
      const target = event.target
      focused.current = target instanceof HTMLElement && panel.current?.contains(target) ? target : null
    }
    const release = (event: PointerEvent) => {
      const target = event.target
      if (
        !(target instanceof Element) ||
        !panel.current?.contains(target) ||
        target.closest('button, input, select, textarea, a, [tabindex]') === null
      ) {
        focused.current = null
      }
    }
    document.addEventListener('focusin', remember)
    document.addEventListener('pointerdown', release, true)
    return () => {
      document.removeEventListener('focusin', remember)
      document.removeEventListener('pointerdown', release, true)
    }
  }, [])
  useLayoutEffect(() => {
    const previous = focused.current
    if (!active || previous === null || previous.isConnected || document.activeElement !== document.body) return
    const target =
      panel.current?.querySelector<HTMLElement>('legend') ?? panel.current?.querySelector<HTMLElement>('h3, h2')
    if (target !== undefined && target !== null) {
      target.tabIndex = -1
      target.focus()
    }
  })
  return panel
}
