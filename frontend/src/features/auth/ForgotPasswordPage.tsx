import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { requestPasswordReset } from './auth-api'
import { emailSchema } from './auth-schemas'

const forgotPasswordSchema = z.object({ email: emailSchema })

export function ForgotPasswordPage(): React.JSX.Element {
  const form = useForm<z.input<typeof forgotPasswordSchema>, unknown, z.output<typeof forgotPasswordSchema>>({
    resolver: zodResolver(forgotPasswordSchema),
    defaultValues: { email: '' },
  })
  const resetRequest = useMutation({ mutationFn: requestPasswordReset })
  const submit = form.handleSubmit(({ email }) => {
    if (!resetRequest.isPending) {
      resetRequest.mutate(email)
    }
  })

  if (resetRequest.isSuccess) {
    // Resposta neutra (API-A06): a mesma mensagem exista ou não a conta.
    return (
      <Page title={messages.forgotPassword.title}>
        <Alert variant="success">{messages.forgotPassword.sent}</Alert>
        <p>
          <TextLink to={paths.login}>{messages.forgotPassword.signInLink}</TextLink>
        </p>
      </Page>
    )
  }

  return (
    <Page title={messages.forgotPassword.title} intro={messages.forgotPassword.intro}>
      {resetRequest.isError ? <RequestFailureAlert failure={classifyRequestFailure(resetRequest.error)} /> : null}
      <form
        noValidate
        method="post"
        aria-busy={resetRequest.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <TextField
          label={messages.forgotPassword.email}
          type="email"
          inputMode="email"
          autoComplete="email"
          error={form.formState.errors.email?.message}
          {...form.register('email')}
        />
        <SubmitButton
          pending={resetRequest.isPending}
          label={messages.forgotPassword.submit}
          pendingLabel={messages.forgotPassword.submitting}
        />
      </form>
      <p>
        <TextLink to={paths.login}>{messages.forgotPassword.signInLink}</TextLink>
      </p>
    </Page>
  )
}
