import { act, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderWithI18n } from '@/test/render'
import { RouteFallback } from './RouteFallback'
import { ROUTE_FALLBACK_DELAY_MS, ROUTE_FALLBACK_MIN_VISIBLE_MS, withFallbackMinimum } from './route-fallback-timing'

describe('RouteFallback (carregando a tela)', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('começa como região de status vazia, sem piscar texto numa carga rápida', () => {
    renderWithI18n(<RouteFallback />)

    expect(screen.getByRole('status')).toBeEmptyDOMElement()
  })

  it('não mostra o aviso um instante antes do atraso', () => {
    renderWithI18n(<RouteFallback />)

    act(() => {
      vi.advanceTimersByTime(ROUTE_FALLBACK_DELAY_MS - 1)
    })

    expect(screen.getByRole('status')).toBeEmptyDOMElement()
  })

  it('anuncia que a página está carregando quando a espera passa do atraso', () => {
    renderWithI18n(<RouteFallback />)

    act(() => {
      vi.advanceTimersByTime(ROUTE_FALLBACK_DELAY_MS)
    })

    expect(screen.getByRole('status')).toHaveTextContent('Carregando a página…')
  })

  it('anuncia no idioma ativo', () => {
    renderWithI18n(<RouteFallback />, 'en')

    act(() => {
      vi.advanceTimersByTime(ROUTE_FALLBACK_DELAY_MS)
    })

    expect(screen.getByRole('status')).toHaveTextContent('Loading the page…')
  })

  it('com o texto na tela, a tela que chega espera o aviso cumprir o tempo mínimo', async () => {
    renderWithI18n(<RouteFallback />)
    act(() => {
      vi.advanceTimersByTime(ROUTE_FALLBACK_DELAY_MS)
    })
    let settled = false
    void withFallbackMinimum(() => Promise.resolve('tela')).then(() => {
      settled = true
    })

    await act(() => vi.advanceTimersByTimeAsync(ROUTE_FALLBACK_MIN_VISIBLE_MS - 1))
    expect(settled).toBe(false)
    await act(() => vi.advanceTimersByTimeAsync(1))
    expect(settled).toBe(true)
  })

  it('antes de o texto aparecer, a tela que chega não espera nada', async () => {
    renderWithI18n(<RouteFallback />)
    let settled = false
    void withFallbackMinimum(() => Promise.resolve('tela')).then(() => {
      settled = true
    })

    await act(() => vi.advanceTimersByTimeAsync(0))

    expect(settled).toBe(true)
  })

  it('ao sair da tela, o aviso deixa de segurar as próximas cargas', async () => {
    const view = renderWithI18n(<RouteFallback />)
    act(() => {
      vi.advanceTimersByTime(ROUTE_FALLBACK_DELAY_MS)
    })
    view.unmount()
    let settled = false
    void withFallbackMinimum(() => Promise.resolve('tela')).then(() => {
      settled = true
    })

    await act(() => vi.advanceTimersByTimeAsync(0))

    expect(settled).toBe(true)
  })
})
