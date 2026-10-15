import { Link } from 'react-router'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Button } from '@/shared/ui/button'
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
      <div className="mt-8 flex flex-wrap gap-4">
        <Button asChild>
          <Link to={paths.register}>{messages.landing.register}</Link>
        </Button>
        <Button asChild variant="secondary">
          <Link to={paths.login}>{messages.landing.signIn}</Link>
        </Button>
      </div>
    </section>
  )
}
