import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { renderWithI18n } from '@/test/render'
import { describe, expect, it, vi } from 'vitest'
import { AreaErrorBoundary } from './error-boundary'

const PRIVATE_DETAIL = 'CANARY-dado-privado-123'

function Exploding({ shouldThrow }: { readonly shouldThrow: () => boolean }): React.JSX.Element {
  if (shouldThrow()) {
    throw new Error(PRIVATE_DETAIL)
  }
  return <p>Conteúdo da área</p>
}

function renderBoundary(shouldThrow: () => boolean) {
  // O React registra o erro capturado no console; aqui isso é esperado e só polui a saída.
  vi.spyOn(console, 'error').mockImplementation(() => undefined)
  return renderWithI18n(
    <MemoryRouter>
      <AreaErrorBoundary>
        <Exploding shouldThrow={shouldThrow} />
      </AreaErrorBoundary>
    </MemoryRouter>,
  )
}

describe('AreaErrorBoundary', () => {
  it('mostra o conteúdo quando nada falha', () => {
    renderBoundary(() => false)

    expect(screen.getByText('Conteúdo da área')).toBeInTheDocument()
  })

  it('troca a área que falhou por um caminho seguro, sem imprimir o erro', () => {
    renderBoundary(() => true)

    expect(screen.getByRole('heading', { level: 1, name: 'Esta parte do Duora parou de funcionar' })).toBeVisible()
    expect(screen.getByRole('link', { name: 'Voltar ao início' })).toHaveAttribute('href', '/')
    expect(document.body).not.toHaveTextContent(PRIVATE_DETAIL)
  })

  it('tentar de novo remonta a área', async () => {
    let failing = true
    renderBoundary(() => failing)

    failing = false
    await userEvent.click(screen.getByRole('button', { name: 'Tentar de novo' }))

    expect(screen.getByText('Conteúdo da área')).toBeInTheDocument()
  })
})
