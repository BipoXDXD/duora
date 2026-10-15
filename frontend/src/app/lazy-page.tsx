import { lazy, type ComponentType, type FunctionComponent, type LazyExoticComponent } from 'react'

export interface LazyPage extends FunctionComponent {
  /** Baixa o módulo sem renderizar; depois dele a tela renderiza direto, sem suspender. */
  readonly preload: () => Promise<void>
}

/**
 * Tela carregada sob demanda (spec 13.5), num chunk próprio. Diferente do `React.lazy` puro, uma falha
 * ao baixar o módulo não fica guardada: o próximo render (o "Tentar de novo" do error boundary da área ou
 * uma nova navegação) ou o próximo `preload` baixa de novo, em vez de repetir o erro até recarregar a página.
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
        loading = undefined
        throw error
      },
    )
    return loading
  }

  function createLazy(): LazyExoticComponent<ComponentType> {
    return lazy(async () => {
      try {
        return { default: await loadOnce() }
      } catch (error) {
        suspending = createLazy()
        throw error
      }
    })
  }

  let suspending = createLazy()

  function LazyPageComponent(): React.JSX.Element {
    const Page = loaded ?? suspending
    return <Page />
  }

  return Object.assign(LazyPageComponent, {
    preload: async (): Promise<void> => {
      await loadOnce()
    },
  })
}
