import { useI18n } from '@/shared/i18n/i18n-context'

interface DocumentTitleProps {
  /** Nome da página; omitido na página inicial, que usa só o nome do produto. */
  readonly page?: string
}

/** Título do documento por rota (WCAG 2.4.2), no idioma ativo; o React 19 o eleva para o <head>. */
export function DocumentTitle({ page }: DocumentTitleProps): React.JSX.Element {
  const { t } = useI18n()
  return <title>{page === undefined ? t('app.name') : t('app.documentTitle', { page })}</title>
}
