import { Suspense } from 'react'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { AreaErrorBoundary } from '@/shared/ui/error-boundary'
import { renderWithI18n } from '@/test/render'
import { lazyPage } from './lazy-page'

function FakePage(): React.JSX.Element {
  return <h1>Página carregada</h1>
}

function renderLazy(Page: React.ComponentType) {
  return renderWithI18n(
    <MemoryRouter>
      <AreaErrorBoundary>
        <Suspense fallback={<p>Esperando o módulo</p>}>
          <Page />
        </Suspense>
      </AreaErrorBoundary>
    </MemoryRouter>,
  )
}

describe('lazyPage', () => {
  it('mostra o fallback enquanto o módulo carrega e depois a página', async () => {
    const Page = lazyPage(() => Promise.resolve(FakePage))

    renderLazy(Page)

    expect(screen.getByText('Esperando o módulo')).toBeInTheDocument()
    expect(await screen.findByRole('heading', { level: 1, name: 'Página carregada' })).toBeInTheDocument()
  })

  it('não carrega o módulo antes de a página ser renderizada', () => {
    const load = vi.fn(() => Promise.resolve(FakePage))

    lazyPage(load)

    expect(load).not.toHaveBeenCalled()
  })

  it('quando o módulo falha, cai no error boundary da área', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const Page = lazyPage(() => Promise.reject(new Error('chunk indisponível')))

    renderLazy(Page)

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Esta parte do Duora parou de funcionar' }),
    ).toBeInTheDocument()
  })

  it('"Tentar de novo" baixa o módulo outra vez depois de uma falha', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    let networkUp = false
    const Page = lazyPage(() => (networkUp ? Promise.resolve(FakePage) : Promise.reject(new Error('rede caiu'))))
    renderLazy(Page)
    const retry = await screen.findByRole('button', { name: 'Tentar de novo' })

    networkUp = true
    await userEvent.click(retry)

    expect(await screen.findByRole('heading', { level: 1, name: 'Página carregada' })).toBeInTheDocument()
  })
  it('depois de pré-carregada, renderiza direto, sem passar pelo fallback', async () => {
    const Page = lazyPage(() => Promise.resolve(FakePage))
    await Page.preload()

    renderLazy(Page)

    expect(screen.queryByText('Esperando o módulo')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Página carregada' })).toBeInTheDocument()
  })

  it('pré-carregar e renderizar baixam o módulo uma vez só', async () => {
    const load = vi.fn(() => Promise.resolve(FakePage))
    const Page = lazyPage(load)

    await Promise.all([Page.preload(), Page.preload()])
    renderLazy(Page)

    expect(load).toHaveBeenCalledTimes(1)
  })

  it('um pré-carregamento que falhou é refeito na próxima tentativa', async () => {
    let networkUp = false
    const Page = lazyPage(() => (networkUp ? Promise.resolve(FakePage) : Promise.reject(new Error('rede caiu'))))
    await expect(Page.preload()).rejects.toThrow('rede caiu')

    networkUp = true
    await Page.preload()
    renderLazy(Page)

    expect(screen.getByRole('heading', { level: 1, name: 'Página carregada' })).toBeInTheDocument()
  })
})
