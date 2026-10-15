import { Link } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'
import { DocumentTitle } from '@/shared/ui/document-title'

export function NotFoundPage(): React.JSX.Element {
  const { t } = useI18n()
  return (
    <section aria-labelledby="titulo-nao-encontrado" className="max-w-prose">
      <DocumentTitle page={t('notFound.title')} />
      <h1 id="titulo-nao-encontrado" className="text-3xl font-semibold tracking-tight">
        {t('notFound.title')}
      </h1>
      <p className="mt-4 text-text-muted">{t('notFound.body')}</p>
      <p className="mt-8">
        <Link
          to="/"
          className="font-semibold text-action underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >
          {t('common.backHome')}
        </Link>
      </p>
    </section>
  )
}
