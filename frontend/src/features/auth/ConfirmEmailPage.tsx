import { useMutation } from '@tanstack/react-query'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { confirmEmail } from './auth-api'
import type { EmailLink } from './auth-schemas'
import { useEmailLinkFromUrl } from './use-email-link'

const PROBLEM_MESSAGES = { INVALID_TOKEN: messages.confirmEmail.invalidToken }

/**
 * Confirmação de e-mail pelo link recebido. Pede um clique em vez de confirmar ao abrir a página:
 * scanners de link que executam JavaScript não confirmam a conta sozinhos (API-A03).
 */
export function ConfirmEmailPage(): React.JSX.Element {
  const link = useEmailLinkFromUrl()

  return (
    <Page title={messages.confirmEmail.title}>
      {link === null ? (
        <>
          <Alert variant="error">{messages.confirmEmail.invalidLink}</Alert>
          <p>
            <TextLink to={paths.login}>{messages.confirmEmail.signInLink}</TextLink>
          </p>
        </>
      ) : (
        <ConfirmEmailForm link={link} />
      )}
    </Page>
  )
}

function ConfirmEmailForm({ link }: { readonly link: EmailLink }): React.JSX.Element {
  const confirmation = useMutation({ mutationFn: () => confirmEmail(link) })

  if (confirmation.isSuccess) {
    return (
      <>
        <Alert variant="success" focusOnMount>
          {messages.confirmEmail.success}
        </Alert>
        <p>
          <TextLink to={paths.login}>{messages.confirmEmail.signInLink}</TextLink>
        </p>
      </>
    )
  }
  return (
    <>
      {confirmation.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(confirmation.error)} problemMessages={PROBLEM_MESSAGES} />
      ) : null}
      <p className="text-text-muted">{messages.confirmEmail.intro}</p>
      <form
        method="post"
        aria-busy={confirmation.isPending}
        onSubmit={(event) => {
          event.preventDefault()
          if (!confirmation.isPending) {
            confirmation.mutate()
          }
        }}
      >
        <SubmitButton
          pending={confirmation.isPending}
          label={messages.confirmEmail.submit}
          pendingLabel={messages.confirmEmail.submitting}
        />
      </form>
    </>
  )
}
