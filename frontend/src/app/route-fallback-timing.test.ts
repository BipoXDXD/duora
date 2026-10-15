import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ROUTE_FALLBACK_MIN_VISIBLE_MS,
  clearRouteFallbackVisible,
  fallbackHoldMs,
  markRouteFallbackVisible,
  withFallbackMinimum,
} from './route-fallback-timing'

/** Chunk que leva `ms` para chegar, como numa rede lenta. */
function chunkArrivingIn(ms: number): () => Promise<string> {
  return () =>
    new Promise((resolve) => {
      setTimeout(() => {
        resolve('tela')
      }, ms)
    })
}

/** Inicia a carga e devolve uma função que diz se ela já entregou a tela. */
function track(result: Promise<string>): () => boolean {
  let settled = false
  void result.then(() => {
    settled = true
  })
  return () => settled
}

describe('fallbackHoldMs', () => {
  it.each([
    { caso: 'sem aviso visível', visibleSince: undefined, now: 5_000, hold: 0 },
    { caso: 'aviso acabou de aparecer', visibleSince: 1_000, now: 1_000, hold: ROUTE_FALLBACK_MIN_VISIBLE_MS },
    { caso: 'aviso visível há 100 ms', visibleSince: 1_000, now: 1_100, hold: ROUTE_FALLBACK_MIN_VISIBLE_MS - 100 },
    { caso: 'falta 1 ms', visibleSince: 1_000, now: 1_000 + ROUTE_FALLBACK_MIN_VISIBLE_MS - 1, hold: 1 },
    { caso: 'mínimo cumprido', visibleSince: 1_000, now: 1_000 + ROUTE_FALLBACK_MIN_VISIBLE_MS, hold: 0 },
    { caso: 'bem depois', visibleSince: 1_000, now: 10_000, hold: 0 },
  ])('$caso: segura por mais o que falta', ({ visibleSince, now, hold }) => {
    expect(fallbackHoldMs(visibleSince, now)).toBe(hold)
  })
})

describe('withFallbackMinimum', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    clearRouteFallbackVisible()
    vi.useRealTimers()
  })

  it('navegação na SPA (sem aviso na tela): chunk de 450 ms entrega em 450 ms, sem espera extra', async () => {
    const settled = track(withFallbackMinimum(chunkArrivingIn(450)))

    await vi.advanceTimersByTimeAsync(450)

    expect(settled()).toBe(true)
  })

  it('com o aviso na tela: entrega só quando ele completa o tempo mínimo visível', async () => {
    markRouteFallbackVisible(performance.now())
    const settled = track(withFallbackMinimum(chunkArrivingIn(10)))

    await vi.advanceTimersByTimeAsync(ROUTE_FALLBACK_MIN_VISIBLE_MS - 1)
    expect(settled()).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    expect(settled()).toBe(true)
  })

  it('o aviso saiu da tela antes de o chunk chegar: entrega na hora', async () => {
    markRouteFallbackVisible(performance.now())
    clearRouteFallbackVisible()
    const settled = track(withFallbackMinimum(chunkArrivingIn(10)))

    await vi.advanceTimersByTimeAsync(10)

    expect(settled()).toBe(true)
  })

  it('falha não espera: o erro chega na hora', async () => {
    markRouteFallbackVisible(performance.now())

    await expect(withFallbackMinimum(() => Promise.reject(new Error('404')))).rejects.toThrow('404')
  })
})
