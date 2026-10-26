import { useAccount } from '@/features/session/account-context'
import type { AccountStatus } from '@/features/session/session-api'
import { useLogout } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { paths } from '@/shared/paths'
import { Page, TextLink } from '@/shared/ui/page'
import { SessionList } from './SessionList'

const STATUS_LABELS: Readonly<Record<AccountStatus, PlainMessageKey>> = {
  PendingEmail: 'account.statusLabel.PendingEmail',
  PendingEligibility: 'account.statusLabel.PendingEligibility',
  Active: 'account.statusLabel.Active',
  Restricted: 'account.statusLabel.Restricted',
  Suspended: 'account.statusLabel.Suspended',
  DeletionPending: 'account.statusLabel.DeletionPending',
  Unknown: 'account.statusLabel.Unknown',
}

/** Área autenticada mínima do M1: a conta própria, as sessões e a saída. */
export function AccountPage(): React.JSX.Element {
  const account = useAccount()
  const logout = useLogout()
  const { t, locale } = useI18n()

  return (
    <Page
      title={t('account.title')}
      width="wide"
      actions={
        <Button
          variant="secondary"
          aria-disabled={logout.isPending}
          onClick={() => {
            if (!logout.isPending) {
              logout.mutate()
            }
          }}
        >
          {logout.isPending ? t('account.signingOut') : t('account.signOut')}
        </Button>
      }
    >
      {logout.isError ? <RequestFailureAlert failure={classifyRequestFailure(logout.error)} /> : null}
      {account.emailConfirmed ? null : <Alert variant="info">{t('account.emailPending')}</Alert>}
      <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t('account.email')}</dt>
        <dd className="break-words text-text-muted">{account.email}</dd>
        <dt className="font-semibold">{t('account.status')}</dt>
        <dd className="text-text-muted">{t(STATUS_LABELS[account.status])}</dd>
      </dl>
      <p>
        <TextLink to={paths.profile}>{t('account.editProfile')}</TextLink>
      </p>
      <p>
        <TextLink to={paths.selfie}>{locale === 'pt-BR' ? 'Confirmar presença' : 'Confirm presence'}</TextLink>
      </p>
      <TextLink to={paths.preferences}>{t('preferences.title')}</TextLink>
      <TextLink to={paths.photos}>{t('photoReview.myPhotos')}</TextLink>
      <TextLink to={paths.events}>{t('participation.events')}</TextLink>
      <SessionList userId={account.id} />
    </Page>
  )
}
