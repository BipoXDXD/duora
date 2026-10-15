import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { useSearchParams } from 'react-router'
import { z } from 'zod'
import { classifyRequestFailure, type RequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { resetPassword } from './auth-api'
import {
  newPasswordSchema,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  readEmailLink,
  type EmailLink,
} from './auth-schemas'

const resetPasswordSchema = z.object({ newPassword: newPasswordSchema })

const PROBLEM_MESSAGES = { INVALID_TOKEN: messages.resetPassword.invalidToken }

export function ResetPasswordPage(): React.JSX.Element {
  const [searchParams] = useSearchParams()
  const link = readEmailLink(searchParams)

  return (
    <Page title={messages.resetPassword.title}>
      {link === null ? (
        <>
          <Alert variant="error">{messages.resetPassword.invalidLink}</Alert>
          <p>
            <TextLink to={paths.forgotPassword}>{messages.resetPassword.requestNewLink}</TextLink>
          </p>
        </>
      ) : (
        <ResetPasswordForm link={link} />
      )}
    </Page>
  )
}

function ResetPasswordForm({ link }: { readonly link: EmailLink }): React.JSX.Element {
  const queryClient = useQueryClient()
  const form = useForm<z.input<typeof resetPasswordSchema>, unknown, z.output<typeof resetPasswordSchema>>({
    resolver: zodResolver(resetPasswordSchema),
    defaultValues: { newPassword: '' },
  })
  const reset = useMutation({
    mutationFn: (newPassword: string) => resetPassword(link, newPassword),
    // A troca de senha pode revogar sessões (API-A07): nada em cache sobrevive a ela.
    onSuccess: () => {
      queryClient.clear()
    },
  })
  const submit = form.handleSubmit(({ newPassword }) => {
    if (!reset.isPending) {
      reset.mutate(newPassword)
    }
  })

  if (reset.isSuccess) {
    return (
      <>
        <Alert variant="success">{messages.resetPassword.success}</Alert>
        <p>
          <TextLink to={paths.login}>{messages.resetPassword.signInLink}</TextLink>
        </p>
      </>
    )
  }

  const failure = reset.isError ? classifyRequestFailure(reset.error) : null
  return (
    <>
      {failure === null ? null : <RequestFailureAlert failure={failure} problemMessages={PROBLEM_MESSAGES} />}
      {isInvalidToken(failure) ? (
        <p>
          <TextLink to={paths.forgotPassword}>{messages.resetPassword.requestNewLink}</TextLink>
        </p>
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={reset.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <PasswordField
          label={messages.resetPassword.newPassword}
          hint={messages.register.passwordHint(PASSWORD_MIN_LENGTH, PASSWORD_MAX_LENGTH)}
          autoComplete="new-password"
          error={form.formState.errors.newPassword?.message}
          {...form.register('newPassword')}
        />
        <SubmitButton
          pending={reset.isPending}
          label={messages.resetPassword.submit}
          pendingLabel={messages.resetPassword.submitting}
        />
      </form>
    </>
  )
}

function isInvalidToken(failure: RequestFailure | null): boolean {
  return failure?.kind === 'problem' && failure.code === 'INVALID_TOKEN'
}
