import { Suspense, useState } from 'react'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import { ChunkLoadError } from '@/shared/ui/chunk-load-error'
import { AreaErrorBoundary } from '@/shared/ui/error-boundary'
import { renderWithI18n } from '@/test/render'
import { lazyPage } from './lazy-page'

const NEW_VERSION_TITLE = 'Há uma versão nova do Duora'

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

    expect(await screen.findByRole('heading', { level: 1, name: NEW_VERSION_TITLE })).toBeInTheDocument()
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

  it('download que falha depois de um tempo: mostra o error boundary e não baixa de novo sozinho', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const load = vi.fn(
      () =>
        new Promise<React.ComponentType>((_resolve, reject) => {
          setTimeout(() => {
            reject(new Error('404 no chunk'))
          }, 5)
        }),
    )
    const Page = lazyPage(load)

    renderLazy(Page)

    expect(await screen.findByRole('heading', { level: 1, name: NEW_VERSION_TITLE })).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('a falha gruda: pré-carregar depois dela rejeita com ChunkLoadError sem baixar de novo', async () => {
    const load = vi.fn(() => Promise.reject(new Error('rede caiu')))
    const Page = lazyPage(load)
    await expect(Page.preload()).rejects.toBeInstanceOf(ChunkLoadError)

    await expect(Page.preload()).rejects.toBeInstanceOf(ChunkLoadError)

    expect(load).toHaveBeenCalledTimes(1)
  })

  it('renderizar e pré-carregar com o download em voo baixam uma vez só', async () => {
    let finish: (page: React.ComponentType) => void = () => undefined
    const load = vi.fn(
      () =>
        new Promise<React.ComponentType>((resolve) => {
          finish = resolve
        }),
    )
    const Page = lazyPage(load)
    renderLazy(Page)

    const preloaded = Page.preload()
    finish(FakePage)
    await preloaded

    expect(await screen.findByRole('heading', { level: 1, name: 'Página carregada' })).toBeInTheDocument()
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('a página carregada mantém o estado quando quem a envolve renderiza de novo', async () => {
    function Counter(): React.JSX.Element {
      const [count, setCount] = useState(0)
      return (
        <button
          type="button"
          onClick={() => {
            setCount(count + 1)
          }}
        >{`Cliques: ${String(count)}`}</button>
      )
    }
    const Page = lazyPage(() => Promise.resolve(Counter))
    const view = renderLazy(Page)
    await userEvent.click(await screen.findByRole('button', { name: 'Cliques: 0' }))

    view.rerender(
      <I18nProvider initialLocale="pt-BR">
        <MemoryRouter>
          <AreaErrorBoundary>
            <Suspense fallback={<p>Esperando o módulo</p>}>
              <Page />
            </Suspense>
          </AreaErrorBoundary>
        </MemoryRouter>
      </I18nProvider>,
    )

    expect(screen.getByRole('button', { name: 'Cliques: 1' })).toBeInTheDocument()
  })
})
