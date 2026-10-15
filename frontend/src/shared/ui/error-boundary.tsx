import { Component, type ReactNode } from 'react'
import { Link } from 'react-router'
import { messages } from '@/shared/messages'
import { Button } from './button'
import { DocumentTitle } from './document-title'

interface AreaErrorBoundaryProps {
  readonly children: ReactNode
}

interface AreaErrorBoundaryState {
  readonly failed: boolean
}

/**
 * Error boundary por área (spec 13.5): uma falha de renderização derruba só a área, nunca a SPA
 * inteira, e o fallback não mostra a mensagem do erro, que pode carregar dado privado.
 * O React só oferece boundaries como componente de classe. Sem telemetria por ora: a spec 19.2
 * ainda vai definir o que pode sair do navegador.
 */
export class AreaErrorBoundary extends Component<AreaErrorBoundaryProps, AreaErrorBoundaryState> {
  override state: AreaErrorBoundaryState = { failed: false }

  static getDerivedStateFromError(): AreaErrorBoundaryState {
    return { failed: true }
  }

  override render(): ReactNode {
    if (!this.state.failed) {
      return this.props.children
    }
    return (
      <AreaErrorFallback
        onRetry={() => {
          this.setState({ failed: false })
        }}
      />
    )
  }
}

function AreaErrorFallback({ onRetry }: { readonly onRetry: () => void }): React.JSX.Element {
  return (
    <section aria-labelledby="titulo-erro-area" className="max-w-prose">
      <DocumentTitle page={messages.errorBoundary.title} />
      <h1 id="titulo-erro-area" className="text-3xl font-semibold tracking-tight">
        {messages.errorBoundary.title}
      </h1>
      <p className="mt-4 text-text-muted">{messages.errorBoundary.description}</p>
      <div className="mt-8 flex flex-wrap items-center gap-4">
        <Button onClick={onRetry}>{messages.common.retry}</Button>
        <Link
          to="/"
          className="inline-flex min-h-11 items-center font-semibold text-action underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >
          {messages.common.backHome}
        </Link>
      </div>
    </section>
  )
}
