import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { renderWithI18n } from '@/test/render'
import { describe, expect, it, vi } from 'vitest'
import { ChunkLoadError } from './chunk-load-error'
import { AreaErrorBoundary } from './error-boundary'
import { reloadPage } from './reload-page'

vi.mock('./reload-page', () => ({ reloadPage: vi.fn() }))

const PRIVATE_DETAIL = 'CANARY-dado-privado-123'

function Exploding({
  shouldThrow,
  error,
}: {
  readonly shouldThrow: () => boolean
  readonly error: Error
}): React.JSX.Element {
  if (shouldThrow()) {
    throw error
  }
  return <p>Conteúdo da área</p>
}

function renderBoundary(shouldThrow: () => boolean, error: Error = new Error(PRIVATE_DETAIL)) {
  // O React registra o erro capturado no console; aqui isso é esperado e só polui a saída.
  vi.spyOn(console, 'error').mockImplementation(() => undefined)
  return renderWithI18n(
    <MemoryRouter>
      <AreaErrorBoundary>
        <Exploding shouldThrow={shouldThrow} error={error} />
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

  it('chunk que não baixa: avisa que há versão nova, sem o detalhe do erro', () => {
    renderBoundary(() => true, new ChunkLoadError(new Error(PRIVATE_DETAIL)))

    expect(screen.getByRole('heading', { level: 1, name: 'Há uma versão nova do Duora' })).toBeVisible()
    expect(screen.getByText('Recarregue a página para continuar de onde parou.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Tentar de novo' })).not.toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(PRIVATE_DETAIL)
  })

  it('chunk que não baixa: o botão recarrega a página em vez de remontar a área', async () => {
    renderBoundary(() => true, new ChunkLoadError(new Error('404')))

    await userEvent.click(screen.getByRole('button', { name: 'Recarregar a página' }))

    expect(reloadPage).toHaveBeenCalledTimes(1)
  })

  it('erro de renderização comum não recarrega a página ao tentar de novo', async () => {
    renderBoundary(() => true)

    await userEvent.click(screen.getByRole('button', { name: 'Tentar de novo' }))

    expect(reloadPage).not.toHaveBeenCalled()
  })
})
