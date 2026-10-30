import { Alert } from '@/shared/ui/alert'
import { useSecurityResultSnapshot } from '@/features/auth/security-result-hooks'
import { SecurityCompletionNotice } from '@/features/auth/SecurityCompletionNotice'
import { useEffect, useState } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { apiClient, classifyRequestFailure, isUnauthorized } from '@/shared/api'
import { TermsReacceptanceNotice } from '@/features/legal/TermsReacceptanceNotice'
import { AccountTimeZoneProvider } from '@/shared/i18n/AccountTimeZoneProvider'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { DocumentTitle } from '@/shared/ui/document-title'
import { AccountContext } from './account-context'
import { isSignInNavigation, readLoginState, type LoginState } from './login-state'
import { useMe } from './session-hooks'
import { useQueryClient } from '@tanstack/react-query'
import { PendingOwnerInstallation, PrivateOperationProvider } from './PrivateOperationProvider'
import { fetchMeForRevalidation, SessionRestrictionError } from './session-api'
import { useSessionContextSnapshot } from './private-operation'
import { meQueryKey } from './query-keys'

/**
 * Rota protegida: só renderiza a área com a identidade confirmada por GET /me. A proteção é de
 * interface; a autorização real é do servidor (spec 2.2). 401 leva à entrada; outra falha mostra
 * erro com nova tentativa, sem confundir rede fora do ar com sessão encerrada.
 */
export function RequireSession(): React.JSX.Element {
  const security = useSecurityResultSnapshot()
  const me = useMe()
  const state = useSessionContextSnapshot()
  const queryClient = useQueryClient()
  const [retryError, setRetryError] = useState<Error | null>(null)
  const [retrying, setRetrying] = useState(false)
  const restrictionError = me.error instanceof SessionRestrictionError ? me.error : null
  const pairedOwner = restrictionError?.owner ?? me.data?.owner
  const [entryHadPendingBootstrap] = useState(() => state.pendingOwner !== null && pairedOwner === state.pendingOwner)
  const active = pairedOwner !== undefined && pairedOwner === state.owner && !state.protected
  const retry = async (): Promise<void> => {
    setRetrying(true)
    setRetryError(null)
    const ticket = apiClient.sessionContext.beginExplicitRevalidation()
    try {
      await queryClient.query({
        queryKey: meQueryKey,
        queryFn: () => fetchMeForRevalidation(ticket),
        structuralSharing: false,
        retry: false,
        staleTime: 0,
      })
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return
      if (!(error instanceof SessionRestrictionError) && error instanceof Error) setRetryError(error)
    } finally {
      setRetrying(false)
    }
  }
  const location = useLocation()
  const { pathname } = location
  const navigationState: unknown = location.state
  const { t, locale, changeLocale } = useI18n()
  const preferredLocale = active ? me.data?.preferredLocale : undefined
  useEffect(() => {
    if (preferredLocale !== undefined && preferredLocale !== null && preferredLocale !== locale)
      changeLocale(preferredLocale)
  }, [preferredLocale, locale, changeLocale])

  useEffect(() => {
    const expected = state.pendingOwner ?? state.owner
    if (expected !== null && pairedOwner !== expected && me.fetchStatus === 'idle') {
      const current = apiClient.sessionContext.getSnapshot()
      if (current.transitionEpoch === state.transitionEpoch && (current.pendingOwner ?? current.owner) === expected)
        apiClient.sessionContext.protect('context')
    }
  }, [state, pairedOwner, me.fetchStatus])

  if (state.pendingOwner !== null && pairedOwner === state.pendingOwner) {
    return (
      <PendingOwnerInstallation
        queryClient={queryClient}
        installation={{ epoch: state.transitionEpoch, owner: state.pendingOwner }}
      >
        <p role="status" data-route-pending className="text-text-muted">
          {t('account.loading')}
        </p>
      </PendingOwnerInstallation>
    )
  }
  if (state.protected && (security.phase === 'pending' || me.fetchStatus === 'fetching')) {
    return (
      <p role="status" data-route-pending className="text-text-muted">
        {t('account.loading')}
      </p>
    )
  }
  const failure = retryError ?? (state.protected && me.error instanceof SessionRestrictionError ? null : me.error)
  const restriction = restrictionError === null ? null : paths.changePassword
  if (failure !== null && isUnauthorized(failure)) {
    return <RedirectToLogin sessionExpired={me.data !== undefined} />
  }
  // A cached owner from an earlier private visit cannot mount before this visit's bootstrap settles.
  if (active && !entryHadPendingBootstrap && !me.isFetchedAfterMount) {
    return (
      <p role="status" data-route-pending className="text-text-muted">
        {t('account.loading')}
      </p>
    )
  }
  if (active && restriction !== null) {
    return (
      <PrivateOperationProvider owner={pairedOwner}>
        {pathname === restriction ? <Outlet /> : <Navigate to={restriction} replace />}
      </PrivateOperationProvider>
    )
  }
  if (active && me.data !== undefined) {
    const needsMfa = !me.data.mfaEnabled && me.data.roles.some((role) => role !== 'participant')
    // Conta suspensa só alcança as telas da allowlist do servidor (spec 8.2, #299); ele nega o resto.
    const suspended = me.data.status === 'Suspended'
    if (suspended && !SUSPENDED_ACCOUNT_PATHS.has(pathname)) {
      // A própria conta e a chegada pelo login não são "área indisponível": a pessoa não pediu outra área.
      const quiet = pathname === paths.account || isSignInNavigation(navigationState)
      const navigation: SuspendedAccountNavigation | undefined = quiet ? undefined : { suspendedFrom: pathname }
      return <Navigate to={paths.accountSuspended} replace state={navigation} />
    }
    if (!suspended && pathname === paths.accountSuspended) return <Navigate to={paths.account} replace />
    if (needsMfa && pathname !== paths.mfaEnrollment) return <Navigate to={paths.mfaEnrollment} replace />
    if (pathname === paths.changePassword) return <Navigate to={paths.account} replace />
    return (
      <PrivateOperationProvider owner={me.data.owner}>
        <AccountContext value={me.data}>
          <AccountTimeZoneProvider timeZone={me.data.preferredTimeZone ?? null}>
            {suspended ? null : <TermsReacceptanceNotice />}
            <Outlet key={state.transitionEpoch} />
          </AccountTimeZoneProvider>
        </AccountContext>
      </PrivateOperationProvider>
    )
  }
  if (failure !== null || state.protected) {
    return (
      <section aria-labelledby="titulo-conta-erro" className="max-w-prose">
        <DocumentTitle page={t('account.title')} />
        <h1 id="titulo-conta-erro" className="text-3xl font-semibold tracking-tight">
          {t('account.title')}
        </h1>
        <SecurityCompletionNotice />
        {state.protected && failure === null && security.phase !== 'failed' ? (
          <Alert variant="info" focusOnMount>
            {t('account.contextUnverified')}
          </Alert>
        ) : null}
        <p className="mt-4 text-text-muted">{t('account.loadError')}</p>
        <div className="mt-4">
          {failure === null ? null : <RequestFailureAlert failure={classifyRequestFailure(failure)} />}
        </div>
        <Button
          variant="secondary"
          disabled={retrying}
          className="mt-6"
          onClick={() => {
            if (!retrying) void retry()
          }}
        >
          {t('common.retry')}
        </Button>
      </section>
    )
  }
  return (
    <p role="status" data-route-pending className="text-text-muted">
      <DocumentTitle page={t('account.title')} />
      {t('account.loading')}
    </p>
  )
}

/**
 * Telas que a conta suspensa ainda abre: a da suspensão (estado da conta e sessões), o MFA e as próprias fotos, as
 * mesmas rotas da allowlist do servidor (ADR-0042).
 */
const SUSPENDED_ACCOUNT_PATHS: ReadonlySet<string> = new Set([
  paths.accountSuspended,
  paths.mfaEnrollment,
  paths.photos,
])

/** Estado da navegação que trouxe a conta suspensa de outra área, para a tela avisar sem dizer por quê. */
export interface SuspendedAccountNavigation {
  readonly suspendedFrom: string
}

/** Leva à entrada; a tela de entrada descarta o cache da identidade que expirou (spec 13.5). */
function RedirectToLogin({ sessionExpired }: { readonly sessionExpired: boolean }): React.JSX.Element {
  const location = useLocation()
  const state: LoginState = {
    ...readLoginState({ from: `${location.pathname}${location.search}${location.hash}` }),
    notice: sessionExpired ? 'session-expired' : 'sign-in-required',
  }
  return <Navigate to={paths.login} replace state={state} />
}
