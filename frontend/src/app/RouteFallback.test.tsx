import { act, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderWithI18n } from '@/test/render'
import { ROUTE_FALLBACK_DELAY_MS, RouteFallback } from './RouteFallback'

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
})
