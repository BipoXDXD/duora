import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { requestPasswordReset } from './auth-api'
import { emailSchema } from './auth-schemas'
import { useValidationMessage } from './validation-message'

const forgotPasswordSchema = z.object({ email: emailSchema })

export function ForgotPasswordPage(): React.JSX.Element {
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const form = useForm<z.input<typeof forgotPasswordSchema>, unknown, z.output<typeof forgotPasswordSchema>>({
    resolver: zodResolver(forgotPasswordSchema),
    defaultValues: { email: '' },
  })
  const resetRequest = usePublicMutation({
    mutationFn: (operation) => requestPasswordReset(operation, forgotPasswordSchema.parse(form.getValues()).email),
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    runPublicOperation((operation) =>
      form.handleSubmit(async () => {
        if (!resetRequest.isPending) await resetRequest.mutateWithOperation(operation, undefined)
      })(event),
    )

  if (resetRequest.isSuccess) {
    // Resposta neutra (API-A06): a mesma mensagem exista ou não a conta.
    return (
      <Page title={t('forgotPassword.title')}>
        <Alert variant="success" focusOnMount>
          {t('forgotPassword.sent')}
        </Alert>
        <p>
          <TextLink to={paths.login}>{t('forgotPassword.signInLink')}</TextLink>
        </p>
      </Page>
    )
  }

  return (
    <Page title={t('forgotPassword.title')} intro={t('forgotPassword.intro')}>
      {resetRequest.isError ? <RequestFailureAlert failure={classifyRequestFailure(resetRequest.error)} /> : null}
      <form
        noValidate
        method="post"
        aria-busy={resetRequest.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event).catch(() => undefined)
        }}
      >
        <TextField
          label={t('forgotPassword.email')}
          type="email"
          inputMode="email"
          autoComplete="email"
          error={fieldError(form.formState.errors.email?.message)}
          {...form.register('email')}
        />
        <SubmitButton
          pending={resetRequest.isPending}
          label={t('forgotPassword.submit')}
          pendingLabel={t('forgotPassword.submitting')}
        />
      </form>
      <p>
        <TextLink to={paths.login}>{t('forgotPassword.signInLink')}</TextLink>
      </p>
    </Page>
  )
}
