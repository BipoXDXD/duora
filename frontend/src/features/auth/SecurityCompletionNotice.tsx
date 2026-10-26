import { useEffect } from 'react'
import { useLocation } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { pageSecurityResult } from './page-security-result'
import { useSecurityResultSnapshot } from './security-result-hooks'

export function SecurityCompletionNotice(): React.JSX.Element | null {
  const state = useSecurityResultSnapshot()
  const { t } = useI18n()
  return state.phase === 'failed' ? (
    <Alert variant="info" focusOnMount>
      {t('mfa.receiptUncertain')}
    </Alert>
  ) : null
}

export function SecurityResultNavigationGuard(): null {
  const { pathname } = useLocation()
  useEffect(() => {
    pageSecurityResult.discard()
  }, [pathname])
  return null
}
