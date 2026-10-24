import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { requestEmailConfirmation } from './auth-api'
import { currentPasswordSchema, emailSchema } from './auth-schemas'
import { useValidationMessage } from './validation-message'

const requestSchema = z.object({ email: emailSchema, password: currentPasswordSchema })

/** Tela neutra depois do cadastro: não confirma nem nega que o e-mail tenha conta (API-A02). */
export function CheckEmailPage(): React.JSX.Element {
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const [requested, setRequested] = useState(false)
  const [failure, setFailure] = useState<ReturnType<typeof classifyRequestFailure> | null>(null)
  const form = useForm<z.input<typeof requestSchema>, unknown, z.output<typeof requestSchema>>({
    resolver: zodResolver(requestSchema),
    defaultValues: { email: '', password: '' },
  })
  const request = useMutation({
    gcTime: 0,
    mutationFn: ({ email, password }: z.output<typeof requestSchema>) => requestEmailConfirmation(email, password),
    onSuccess: () => {
      form.reset()
      setFailure(null)
      setRequested(true)
      request.reset()
    },
    onError: (error) => {
      form.resetField('password')
      setFailure(classifyRequestFailure(error))
      request.reset()
    },
  })
  const submit = form.handleSubmit((values) => {
    if (!request.isPending) request.mutate(values)
  })
  return (
    <Page title={t('checkEmail.title')} intro={t('checkEmail.body')}>
      <p className="text-text-muted">{t('checkEmail.hint')}</p>
      {requested ? (
        <Alert variant="success" focusOnMount>
          {t('checkEmail.resendAccepted')}
        </Alert>
      ) : (
        <>
          {failure ? <RequestFailureAlert failure={failure} /> : null}
          <form
            noValidate
            method="post"
            aria-busy={request.isPending}
            className="flex flex-col gap-6"
            onSubmit={(event) => {
              void submit(event)
            }}
          >
            <TextField
              label={t('login.email')}
              type="email"
              inputMode="email"
              autoComplete="email"
              error={fieldError(form.formState.errors.email?.message)}
              {...form.register('email')}
            />
            <PasswordField
              label={t('login.password')}
              autoComplete="current-password"
              error={fieldError(form.formState.errors.password?.message)}
              {...form.register('password')}
            />
            <SubmitButton
              pending={request.isPending}
              label={t('checkEmail.resend')}
              pendingLabel={t('checkEmail.resending')}
            />
          </form>
        </>
      )}
      <p>
        <TextLink to={paths.register}>{t('checkEmail.requestAgain')}</TextLink>
      </p>
      <p>
        <TextLink to={paths.login}>{t('checkEmail.signInLink')}</TextLink>
      </p>
    </Page>
  )
}
