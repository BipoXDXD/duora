import { useAccount } from '@/features/session/account-context'
import type { SuspensionReason } from '@/features/session/session-api'
import { useLogout } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { useLocation } from 'react-router'
import type { SuspendedAccountNavigation } from '@/features/session/RequireSession'
import { paths } from '@/shared/paths'
import { Page, TextLink } from '@/shared/ui/page'
import { SessionList } from './SessionList'

const REASON_LABELS: Readonly<Record<SuspensionReason, PlainMessageKey>> = {
  TermsViolation: 'accountSuspended.reason.TermsViolation',
  SelfieVerificationFailed: 'accountSuspended.reason.SelfieVerificationFailed',
  Unknown: 'accountSuspended.reason.Unknown',
}

/**
 * Conta suspensa (issue #299; spec 8.2): a sessão só alcança o estado da própria conta e a segurança dela. A tela
 * mostra o motivo em código e o instante, nunca o motivo interno, e mantém as sessões e a saída ao alcance. O
 * servidor é quem nega o resto (403 ACCOUNT_SUSPENDED); a guarda da área só evita telas que falhariam.
 */
export function SuspendedAccountPage(): React.JSX.Element {
  const account = useAccount()
  const logout = useLogout()
  const { t, formatDateTime } = useI18n()
  const suspension = account.suspension ?? null
  const location = useLocation()
  const redirected = isSuspendedNavigation(location.state)

  return (
    <Page
      title={t('accountSuspended.title')}
      intro={t('accountSuspended.intro')}
      width="wide"
      actions={
        <Button
          variant="secondary"
          aria-disabled={logout.isPending}
          onClick={() => {
            if (!logout.isPending) logout.mutate()
          }}
        >
          {logout.isPending ? t('account.signingOut') : t('account.signOut')}
        </Button>
      }
    >
      {logout.isError ? <RequestFailureAlert failure={classifyRequestFailure(logout.error)} /> : null}
      {redirected ? (
        <Alert variant="info" focusOnMount>
          {t('accountSuspended.areaUnavailable')}
        </Alert>
      ) : null}
      <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{t('accountSuspended.reason')}</dt>
        <dd className="text-text-muted">{t(REASON_LABELS[suspension?.reasonCode ?? 'Unknown'])}</dd>
        {suspension === null ? null : (
          <>
            <dt className="font-semibold">{t('accountSuspended.since')}</dt>
            <dd className="text-text-muted">
              <time dateTime={suspension.suspendedAt}>{formatDateTime(new Date(suspension.suspendedAt))}</time>
            </dd>
          </>
        )}
      </dl>
      <p>{t('accountSuspended.stillAvailable')}</p>
      <ul className="flex flex-col gap-1">
        <li>
          <TextLink to={paths.mfaEnrollment}>{t('accountSuspended.mfaLink')}</TextLink>
        </li>
        <li>
          <TextLink to={paths.photos}>{t('photoReview.myPhotos')}</TextLink>
        </li>
      </ul>
      <Alert variant="info">{t('accountSuspended.appeal')}</Alert>
      <SessionList userId={account.id} />
    </Page>
  )
}

function isSuspendedNavigation(state: unknown): state is SuspendedAccountNavigation {
  return typeof state === 'object' && state !== null && 'suspendedFrom' in state
}
