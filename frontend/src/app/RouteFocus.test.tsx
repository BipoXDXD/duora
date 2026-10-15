import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { renderApp } from '@/test/render'

describe('mudança de rota (A11Y-02)', () => {
  it('na primeira carga não rouba o foco nem anuncia', async () => {
    await renderApp('/')

    expect(document.body).toHaveFocus()
    expect(screen.getByTestId('anuncio-de-rota')).toBeEmptyDOMElement()
  })

  it('ao navegar, move o foco para o h1 da página nova e anuncia o título', async () => {
    const { user } = await renderApp('/entrar')

    await user.click(screen.getByRole('link', { name: 'Criar conta' }))

    const heading = await screen.findByRole('heading', { level: 1, name: 'Criar conta' })
    await waitFor(() => {
      expect(heading).toHaveFocus()
    })
    expect(heading).toHaveAttribute('tabindex', '-1')
    expect(screen.getByTestId('anuncio-de-rota')).toHaveTextContent('Página carregada: Criar conta · Duora')
  })

  it('a região de anúncio é discreta e educada (aria-live polite)', async () => {
    await renderApp('/')

    const region = screen.getByTestId('anuncio-de-rota')
    expect(region).toHaveAttribute('aria-live', 'polite')
    expect(region).toHaveClass('sr-only')
  })
})
