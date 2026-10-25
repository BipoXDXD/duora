import { apiClient } from '@/shared/api'
import { identityBroadcast } from '@/features/session/identity-broadcast'
import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure, type RequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { resetPassword } from './auth-api'
import { newPasswordSchema, PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH, type EmailLink } from './auth-schemas'
import { useEmailLinkFromUrl } from './use-email-link'
import { useValidationMessage } from './validation-message'

const resetPasswordSchema = z.object({ newPassword: newPasswordSchema })

const PROBLEM_MESSAGES = { INVALID_TOKEN: 'resetPassword.invalidToken' } as const

export function ResetPasswordPage(): React.JSX.Element {
  const link = useEmailLinkFromUrl()
  const { t } = useI18n()

  return (
    <Page title={t('resetPassword.title')}>
      {link === null ? (
        <>
          <Alert variant="error">{t('resetPassword.invalidLink')}</Alert>
          <p>
            <TextLink to={paths.forgotPassword}>{t('resetPassword.requestNewLink')}</TextLink>
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
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const form = useForm<z.input<typeof resetPasswordSchema>, unknown, z.output<typeof resetPasswordSchema>>({
    resolver: zodResolver(resetPasswordSchema),
    defaultValues: { newPassword: '' },
  })
  const reset = usePublicMutation({
    mutationFn: (operation) => resetPassword(operation, link, resetPasswordSchema.parse(form.getValues()).newPassword),
    gcTime: 0,
    // A troca de senha pode revogar sessões (API-A07): nada em cache sobrevive a ela.
    onSuccess: () => {
      apiClient.sessionContext.protect('identity')
      identityBroadcast.announce()
      queryClient.clear()
    },
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    runPublicOperation((operation) =>
      form.handleSubmit(async () => {
        if (!reset.isPending) await reset.mutateWithOperation(operation, undefined)
      })(event),
    )

  if (reset.isSuccess) {
    return (
      <>
        <Alert variant="success" focusOnMount>
          {t('resetPassword.success')}
        </Alert>
        <p>
          <TextLink to={paths.login}>{t('resetPassword.signInLink')}</TextLink>
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
          <TextLink to={paths.forgotPassword}>{t('resetPassword.requestNewLink')}</TextLink>
        </p>
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={reset.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event).catch(() => undefined)
        }}
      >
        <PasswordField
          label={t('resetPassword.newPassword')}
          hint={t('register.passwordHint', { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH })}
          autoComplete="new-password"
          error={fieldError(form.formState.errors.newPassword?.message)}
          {...form.register('newPassword')}
        />
        <SubmitButton
          pending={reset.isPending}
          label={t('resetPassword.submit')}
          pendingLabel={t('resetPassword.submitting')}
        />
      </form>
    </>
  )
}

function isInvalidToken(failure: RequestFailure | null): boolean {
  return failure?.kind === 'problem' && failure.code === 'INVALID_TOKEN'
}
