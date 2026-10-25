import { LegalNavigation } from '@/features/legal/LegalNavigation'
import { Link } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Button } from '@/shared/ui/button'
import { DocumentTitle } from '@/shared/ui/document-title'

export function LandingPage(): React.JSX.Element {
  const { t } = useI18n()
  return (
    <section aria-labelledby="titulo-inicio" className="max-w-prose">
      <DocumentTitle />
      <h1 id="titulo-inicio" className="text-4xl font-semibold tracking-tight">
        {t('app.name')}
      </h1>
      <p className="mt-4 text-lg text-text-muted">{t('landing.intro')}</p>
      <div className="mt-8 flex flex-wrap gap-4">
        <Button asChild>
          <Link to={paths.register}>{t('landing.register')}</Link>
        </Button>
        <Button asChild variant="secondary">
          <Link to={paths.login}>{t('landing.signIn')}</Link>
        </Button>
      </div>
      <LegalNavigation />
    </section>
  )
}
