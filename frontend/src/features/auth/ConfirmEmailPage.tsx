import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure, type RequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { confirmEmail } from './auth-api'
import { currentPasswordSchema, type EmailLink } from './auth-schemas'
import { useEmailLinkFromUrl } from './use-email-link'

const PROBLEM_MESSAGES = { INVALID_TOKEN: messages.confirmEmail.invalidToken }

const confirmEmailSchema = z.object({ password: currentPasswordSchema })

/**
 * Confirmação de e-mail pelo link recebido. Pede um clique em vez de confirmar ao abrir a página
 * (scanners de link que executam JavaScript não confirmam a conta sozinhos, API-A03) e a senha do
 * cadastro (um link gerado por terceiro não confirma a conta na caixa de outra pessoa).
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
  const form = useForm<z.input<typeof confirmEmailSchema>, unknown, z.output<typeof confirmEmailSchema>>({
    resolver: zodResolver(confirmEmailSchema),
    defaultValues: { password: '' },
  })
  const confirmation = useMutation({ mutationFn: (password: string) => confirmEmail(link, password) })
  const submit = form.handleSubmit(({ password }) => {
    if (!confirmation.isPending) {
      confirmation.mutate(password)
    }
  })

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
  const failure = confirmation.isError ? classifyRequestFailure(confirmation.error) : null
  return (
    <>
      {failure === null ? null : <RequestFailureAlert failure={failure} problemMessages={PROBLEM_MESSAGES} />}
      {isInvalidToken(failure) ? (
        <p>
          <TextLink to={paths.register}>{messages.confirmEmail.registerAgain}</TextLink>
        </p>
      ) : null}
      <p className="text-text-muted">{messages.confirmEmail.intro}</p>
      <form
        noValidate
        method="post"
        aria-busy={confirmation.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <PasswordField
          label={messages.confirmEmail.password}
          hint={messages.confirmEmail.passwordHint}
          autoComplete="current-password"
          error={form.formState.errors.password?.message}
          {...form.register('password')}
        />
        <SubmitButton
          pending={confirmation.isPending}
          label={messages.confirmEmail.submit}
          pendingLabel={messages.confirmEmail.submitting}
        />
      </form>
    </>
  )
}

function isInvalidToken(failure: RequestFailure | null): boolean {
  return failure?.kind === 'problem' && failure.code === 'INVALID_TOKEN'
}
