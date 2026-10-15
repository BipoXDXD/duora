import { Component, type ReactNode } from 'react'
import { Link } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from './button'
import { ChunkLoadError } from './chunk-load-error'
import { DocumentTitle } from './document-title'
import { reloadPage } from './reload-page'

interface AreaErrorBoundaryProps {
  readonly children: ReactNode
}

/** `chunk`: o código da tela não baixou (versão nova); `render`: a tela falhou ao renderizar. */
interface AreaErrorBoundaryState {
  readonly failure: 'none' | 'chunk' | 'render'
}

/**
 * Error boundary por área (spec 13.5): uma falha de renderização derruba só a área, nunca a SPA
 * inteira, e o fallback não mostra a mensagem do erro, que pode carregar dado privado.
 * O React só oferece boundaries como componente de classe. Sem telemetria por ora: a spec 19.2
 * ainda vai definir o que pode sair do navegador.
 */
export class AreaErrorBoundary extends Component<AreaErrorBoundaryProps, AreaErrorBoundaryState> {
  override state: AreaErrorBoundaryState = { failure: 'none' }

  static getDerivedStateFromError(error: unknown): AreaErrorBoundaryState {
    return { failure: error instanceof ChunkLoadError ? 'chunk' : 'render' }
  }

  override render(): ReactNode {
    if (this.state.failure === 'none') {
      return this.props.children
    }
    if (this.state.failure === 'chunk') {
      return <NewVersionFallback />
    }
    return (
      <AreaErrorFallback
        onRetry={() => {
          this.setState({ failure: 'none' })
        }}
      />
    )
  }
}

function AreaErrorFallback({ onRetry }: { readonly onRetry: () => void }): React.JSX.Element {
  const { t } = useI18n()
  return (
    <section aria-labelledby="titulo-erro-area" className="max-w-prose">
      <DocumentTitle page={t('errorBoundary.title')} />
      <h1 id="titulo-erro-area" className="text-3xl font-semibold tracking-tight">
        {t('errorBoundary.title')}
      </h1>
      <p className="mt-4 text-text-muted">{t('errorBoundary.description')}</p>
      <div className="mt-8 flex flex-wrap items-center gap-4">
        <Button onClick={onRetry}>{t('common.retry')}</Button>
        <Link
          to="/"
          className="inline-flex min-h-11 items-center font-semibold text-action underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >
          {t('common.backHome')}
        </Link>
      </div>
    </section>
  )
}

/** Remontar a área não adianta: o navegador guarda o módulo que falhou. Só recarregar busca a versão nova. */
function NewVersionFallback(): React.JSX.Element {
  const { t } = useI18n()
  return (
    <section aria-labelledby="titulo-versao-nova" className="max-w-prose">
      <DocumentTitle page={t('errorBoundary.newVersionTitle')} />
      <h1 id="titulo-versao-nova" className="text-3xl font-semibold tracking-tight">
        {t('errorBoundary.newVersionTitle')}
      </h1>
      <p className="mt-4 text-text-muted">{t('errorBoundary.newVersionDescription')}</p>
      <div className="mt-8">
        <Button onClick={reloadPage}>{t('errorBoundary.reload')}</Button>
      </div>
    </section>
  )
}
