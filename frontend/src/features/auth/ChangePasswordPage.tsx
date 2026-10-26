import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { useSessionTransitions } from '@/features/session/session-hooks'
import { ApiProblemError, classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField } from '@/shared/ui/form-fields'
import { Page, SubmitButton } from '@/shared/ui/page'
import { changePassword } from './auth-api'
import { SignInAgainButton } from './SignInAgainButton'
import { currentPasswordSchema, newPasswordSchema, PASSWORD_MIN_LENGTH, PASSWORD_MAX_LENGTH } from './auth-schemas'
import { useValidationMessage } from './validation-message'

const schema = z
  .object({
    currentPassword: currentPasswordSchema,
    newPassword: newPasswordSchema,
    confirmation: currentPasswordSchema,
  })
  .refine((value) => value.newPassword === value.confirmation, {
    path: ['confirmation'],
    message: 'validation.passwordMismatch',
  })

export function ChangePasswordPage(): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const transitions = useSessionTransitions()
  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: { currentPassword: '', newPassword: '', confirmation: '' },
  })
  const change = usePrivateMutation({
    // Senhas ficam somente no formulário, nunca em variables do MutationCache.
    mutationFn: async (operation) => {
      const { currentPassword, newPassword } = form.getValues()
      await changePassword(operation, currentPassword, newPassword)
    },
    gcTime: 0,
    onSuccess: async () => {
      form.reset()
      // O backend já renovou o cookie. /me decide o próximo passo com a sessão nova.
      await transitions.signedIn(paths.account)
    },
  })
  const reused =
    change.error instanceof ApiProblemError &&
    change.error.problem.errors?.newPassword?.includes('SAME_AS_TEMPORARY_PASSWORD')
  const submit = (event: React.BaseSyntheticEvent) =>
    privateOperation.run((operation) =>
      form.handleSubmit(async () => {
        if (!change.isPending) await change.mutateWithOperation(operation, undefined)
      })(event),
    )
  return (
    <Page title={t('changePassword.title')}>
      <p>{t('changePassword.instructions')}</p>
      {reused ? (
        <Alert variant="error">{t('changePassword.reused')}</Alert>
      ) : change.isError ? (
        <RequestFailureAlert
          failure={classifyRequestFailure(change.error)}
          problemMessages={{
            INVALID_CREDENTIALS: 'changePassword.invalidCredentials',
            REAUTHENTICATION_REQUIRED: 'changePassword.reauthenticate',
            CSRF_VALIDATION_FAILED: 'changePassword.csrf',
          }}
        />
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={change.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event).catch(() => undefined)
        }}
      >
        <PasswordField
          label={t('changePassword.currentPassword')}
          autoComplete="current-password"
          error={fieldError(form.formState.errors.currentPassword?.message)}
          {...form.register('currentPassword')}
        />
        <PasswordField
          label={t('resetPassword.newPassword')}
          autoComplete="new-password"
          hint={t('register.passwordHint', { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH })}
          error={fieldError(form.formState.errors.newPassword?.message)}
          {...form.register('newPassword')}
        />
        <PasswordField
          label={t('changePassword.confirmation')}
          autoComplete="new-password"
          error={fieldError(form.formState.errors.confirmation?.message)}
          {...form.register('confirmation')}
        />
        <SubmitButton
          pending={change.isPending}
          label={t('resetPassword.submit')}
          pendingLabel={t('resetPassword.submitting')}
        />
      </form>
      <SignInAgainButton />
    </Page>
  )
}
