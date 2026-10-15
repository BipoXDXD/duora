import { useAccount } from '@/features/session/account-context'
import { useLogout } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page } from '@/shared/ui/page'
import { SessionList } from './SessionList'

/** Área autenticada mínima do M1: a conta própria, as sessões e a saída. */
export function AccountPage(): React.JSX.Element {
  const account = useAccount()
  const logout = useLogout()

  return (
    <Page
      title={messages.account.title}
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
          {logout.isPending ? messages.account.signingOut : messages.account.signOut}
        </Button>
      }
    >
      {logout.isError ? <RequestFailureAlert failure={classifyRequestFailure(logout.error)} /> : null}
      {account.emailConfirmed ? null : <Alert variant="info">{messages.account.emailPending}</Alert>}
      <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <dt className="font-semibold">{messages.account.email}</dt>
        <dd className="break-words text-text-muted">{account.email}</dd>
        <dt className="font-semibold">{messages.account.status}</dt>
        <dd className="text-text-muted">{messages.account.statusLabels[account.status]}</dd>
      </dl>
      <SessionList userId={account.id} />
    </Page>
  )
}
