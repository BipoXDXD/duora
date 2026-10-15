import { DocumentTitle } from '@/shared/ui/document-title'

export function LandingPage(): React.JSX.Element {
  return (
    <section aria-labelledby="titulo-inicio" className="max-w-prose">
      <DocumentTitle />
      <h1 id="titulo-inicio" className="text-4xl font-semibold tracking-tight">
        Duora
      </h1>
      <p className="mt-4 text-lg text-text-muted">
        Encontros entre duas pessoas adultas, em eventos com rodadas e experiências cooperativas. O interesse é privado
        e a conversa só continua quando ele é mútuo.
      </p>
      <p className="mt-8 rounded-md bg-surface-muted px-4 py-3 text-sm text-text-muted">
        Em construção: cadastro e eventos ainda não estão disponíveis.
      </p>
    </section>
  )
}
