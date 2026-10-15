import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Page, TextLink } from '@/shared/ui/page'

/** Tela neutra depois do cadastro: não confirma nem nega que o e-mail tenha conta (API-A02). */
export function CheckEmailPage(): React.JSX.Element {
  const { t } = useI18n()
  return (
    <Page title={t('checkEmail.title')} intro={t('checkEmail.body')}>
      <p className="text-text-muted">{t('checkEmail.hint')}</p>
      <p>
        <TextLink to={paths.login}>{t('checkEmail.signInLink')}</TextLink>
      </p>
    </Page>
  )
}
