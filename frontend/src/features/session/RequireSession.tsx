import { Navigate, Outlet, useLocation } from 'react-router'
import { classifyRequestFailure, isUnauthorized } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { DocumentTitle } from '@/shared/ui/document-title'
import { AccountContext } from './account-context'
import type { LoginState } from './login-state'
import { useMe } from './session-hooks'

/**
 * Rota protegida: só renderiza a área com a identidade confirmada por GET /me. A proteção é de
 * interface; a autorização real é do servidor (spec 2.2). 401 leva à entrada; outra falha mostra
 * erro com nova tentativa, sem confundir rede fora do ar com sessão encerrada.
 */
export function RequireSession(): React.JSX.Element {
  const me = useMe()
  const { t } = useI18n()

  if (me.isError && isUnauthorized(me.error)) {
    return <RedirectToLogin sessionExpired={me.data !== undefined} />
  }
  if (me.data !== undefined) {
    return (
      <AccountContext value={me.data}>
        <Outlet />
      </AccountContext>
    )
  }
  if (me.isError) {
    return (
      <section aria-labelledby="titulo-conta-erro" className="max-w-prose">
        <DocumentTitle page={t('account.title')} />
        <h1 id="titulo-conta-erro" className="text-3xl font-semibold tracking-tight">
          {t('account.title')}
        </h1>
        <p className="mt-4 text-text-muted">{t('account.loadError')}</p>
        <div className="mt-4">
          <RequestFailureAlert failure={classifyRequestFailure(me.error)} />
        </div>
        <Button
          variant="secondary"
          className="mt-6"
          onClick={() => {
            void me.refetch()
          }}
        >
          {t('common.retry')}
        </Button>
      </section>
    )
  }
  return (
    <p role="status" className="text-text-muted">
      <DocumentTitle page={t('account.title')} />
      {t('account.loading')}
    </p>
  )
}

/** Leva à entrada; a tela de entrada descarta o cache da identidade que expirou (spec 13.5). */
function RedirectToLogin({ sessionExpired }: { readonly sessionExpired: boolean }): React.JSX.Element {
  const location = useLocation()
  const state: LoginState = {
    notice: sessionExpired ? 'session-expired' : 'sign-in-required',
    from: `${location.pathname}${location.search}`,
  }
  return <Navigate to={paths.login} replace state={state} />
}
