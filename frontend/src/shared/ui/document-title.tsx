const PRODUCT_NAME = 'Duora'

interface DocumentTitleProps {
  /** Nome da página; omitido na página inicial, que usa só o nome do produto. */
  readonly page?: string
}

/** Título do documento por rota (WCAG 2.4.2); o React 19 o eleva para o <head>. */
export function DocumentTitle({ page }: DocumentTitleProps): React.JSX.Element {
  return <title>{page === undefined ? PRODUCT_NAME : `${page} · ${PRODUCT_NAME}`}</title>
}
