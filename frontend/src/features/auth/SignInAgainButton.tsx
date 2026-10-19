import { useLogout } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'

/**
 * Saída das fases restritas (troca da senha inicial e cadastro de MFA): encerra a sessão no servidor (A05) antes de
 * ir à entrada. Só navegar deixaria a sessão restrita viva até expirar (auth.md, prazo da troca).
 */
export function SignInAgainButton(): React.JSX.Element {
  const { t } = useI18n()
  const logout = useLogout()
  return (
    <div className="flex flex-col items-start gap-4">
      {logout.isError ? <RequestFailureAlert failure={classifyRequestFailure(logout.error)} /> : null}
      <Button
        variant="neutral"
        aria-disabled={logout.isPending}
        onClick={() => {
          if (!logout.isPending) logout.mutate()
        }}
      >
        {logout.isPending ? t('account.signingOut') : t('changePassword.signInAgain')}
      </Button>
    </div>
  )
}
