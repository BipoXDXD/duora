import { Link } from 'react-router'
import { DocumentTitle } from '@/shared/ui/document-title'

export function NotFoundPage(): React.JSX.Element {
  return (
    <section aria-labelledby="titulo-nao-encontrado" className="max-w-prose">
      <DocumentTitle page="Página não encontrada" />
      <h1 id="titulo-nao-encontrado" className="text-3xl font-semibold tracking-tight">
        Página não encontrada
      </h1>
      <p className="mt-4 text-text-muted">O endereço não corresponde a nenhuma página do Duora.</p>
      <p className="mt-8">
        <Link
          to="/"
          className="font-semibold text-action underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >
          Voltar ao início
        </Link>
      </p>
    </section>
  )
}
