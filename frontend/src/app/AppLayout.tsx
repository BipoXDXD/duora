import { Link, Outlet } from 'react-router'

const MAIN_CONTENT_ID = 'conteudo-principal'

export function AppLayout(): React.JSX.Element {
  return (
    <div className="flex min-h-svh flex-col">
      <a
        href={`#${MAIN_CONTENT_ID}`}
        className="sr-only rounded-md bg-surface px-4 py-3 font-semibold text-action focus-visible:not-sr-only focus-visible:absolute focus-visible:top-4 focus-visible:left-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
      >
        Pular para o conteúdo
      </a>
      <header className="border-b border-border">
        <div className="mx-auto flex max-w-5xl items-center px-4 py-3">
          <Link
            to="/"
            className="inline-flex min-h-11 items-center rounded-md text-xl font-semibold text-text focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          >
            Duora
          </Link>
        </div>
      </header>
      <main id={MAIN_CONTENT_ID} tabIndex={-1} className="mx-auto w-full max-w-5xl flex-1 px-4 py-12">
        <Outlet />
      </main>
      <footer className="border-t border-border bg-surface-muted">
        <p className="mx-auto max-w-5xl px-4 py-6 text-sm text-text-muted">Duora · versão em desenvolvimento</p>
      </footer>
    </div>
  )
}
