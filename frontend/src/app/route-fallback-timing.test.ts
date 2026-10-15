import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ROUTE_FALLBACK_DELAY_MS,
  ROUTE_FALLBACK_MIN_VISIBLE_MS,
  fallbackHoldMs,
  withFallbackMinimum,
} from './route-fallback-timing'

describe('fallbackHoldMs', () => {
  it.each([
    [0, 0],
    [ROUTE_FALLBACK_DELAY_MS - 1, 0],
    [ROUTE_FALLBACK_DELAY_MS, ROUTE_FALLBACK_MIN_VISIBLE_MS],
    [ROUTE_FALLBACK_DELAY_MS + 100, ROUTE_FALLBACK_MIN_VISIBLE_MS - 100],
    [ROUTE_FALLBACK_DELAY_MS + ROUTE_FALLBACK_MIN_VISIBLE_MS - 1, 1],
    [ROUTE_FALLBACK_DELAY_MS + ROUTE_FALLBACK_MIN_VISIBLE_MS, 0],
    [10_000, 0],
  ])('carga de %i ms segura o aviso por mais %i ms', (elapsed, hold) => {
    expect(fallbackHoldMs(elapsed)).toBe(hold)
  })
})

describe('withFallbackMinimum', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('carga rápida entrega na hora, sem atraso', async () => {
    const result = withFallbackMinimum(() => Promise.resolve('tela'))

    await expect(result).resolves.toBe('tela')
  })

  it('carga que chega logo depois de o aviso aparecer espera o aviso cumprir o mínimo', async () => {
    let settled = false
    const result = withFallbackMinimum(
      () =>
        new Promise<string>((resolve) => {
          setTimeout(() => {
            resolve('tela')
          }, ROUTE_FALLBACK_DELAY_MS + 10)
        }),
    ).then((value) => {
      settled = true
      return value
    })

    await vi.advanceTimersByTimeAsync(ROUTE_FALLBACK_DELAY_MS + ROUTE_FALLBACK_MIN_VISIBLE_MS - 1)
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    await expect(result).resolves.toBe('tela')
  })

  it('falha não espera: o erro chega na hora', async () => {
    const result = withFallbackMinimum(() => Promise.reject(new Error('404')))

    await expect(result).rejects.toThrow('404')
  })
})
