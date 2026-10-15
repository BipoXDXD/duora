import { lazy, type ComponentType, type FunctionComponent } from 'react'
import { ChunkLoadError } from '@/shared/ui/chunk-load-error'

export interface LazyPage extends FunctionComponent {
  /** Baixa o módulo sem renderizar; depois dele a tela renderiza direto, sem suspender. */
  readonly preload: () => Promise<void>
}

/**
 * Tela carregada sob demanda (spec 13.5), num chunk próprio. Render e `preload` compartilham um único
 * download. Uma falha gruda, como no `React.lazy`: o navegador também guarda o módulo que falhou (WebKit),
 * então baixar de novo na mesma página não adianta. Ela vira `ChunkLoadError`, e o error boundary da área
 * oferece recarregar a página.
 */
export function lazyPage(load: () => Promise<ComponentType>): LazyPage {
  let loaded: ComponentType | undefined
  let loading: Promise<ComponentType> | undefined

  function loadOnce(): Promise<ComponentType> {
    loading ??= load().then(
      (component) => {
        loaded = component
        return component
      },
      (error: unknown) => {
        throw new ChunkLoadError(error)
      },
    )
    return loading
  }

  const Suspending = lazy(async () => ({ default: await loadOnce() }))

  function LazyPageComponent(): React.JSX.Element {
    // Com o módulo já em mãos, a tela entra sem suspender; o React reconcilia o lazy resolvido com o mesmo
    // componente, então o estado da tela sobrevive à troca.
    const Page = loaded ?? Suspending
    return <Page />
  }

  return Object.assign(LazyPageComponent, {
    preload: async (): Promise<void> => {
      await loadOnce()
    },
  })
}
