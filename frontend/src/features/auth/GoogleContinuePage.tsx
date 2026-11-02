import { useEffect, useState } from 'react'
import { Navigate } from 'react-router'
import { z } from 'zod'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { useSessionTransitions } from '@/features/session/session-hooks'
import { ApiProblemError, classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { currentPasswordSchema, recoveryCodeSchema, totpCodeSchema } from './auth-schemas'
import { completeGoogle, confirmGoogleEmail, useGoogleFlow, useGoogleOptions, useGoogleFlowExpired } from './google-api'
import { GoogleAuthButton } from './GoogleAuthButton'
import { RegisterPage } from './RegisterPage'
import { useValidationMessage } from './validation-message'

export function GoogleContinuePage(): React.JSX.Element {
  const { t } = useI18n()
  const options = useGoogleOptions()
  const flow = useGoogleFlow(options.data?.enabled === true)
  const current = flow.data
  const expired = useGoogleFlowExpired(current)
  if (options.isPending || (options.data?.enabled && flow.isPending))
    return (
      <Page title={t('google.title')}>
        <p role="status">{t('account.loading')}</p>
      </Page>
    )
  if (options.isError || !options.data?.enabled || flow.isError || !current || expired || current.nextStep === 'Failed')
    return (
      <Page title={t('google.title')}>
        <Alert variant="error">{t('google.failed')}</Alert>
        <TextLink to={paths.login}>{t('login.title')}</TextLink>
      </Page>
    )
  if (current.nextStep === 'Link' && options.data.linkingEnabled)
    return <Navigate to={paths.googleLinkContinue} replace />
  if (current.nextStep === 'Register' && options.data.registrationEnabled && current.email)
    return <RegisterPage googleEmail={current.email} onGoogleRegistered={() => flow.refetch()} />
  return (
    <Page title={t('google.title')}>
      {current.nextStep === 'Login' || current.nextStep === 'Mfa' ? (
        <GoogleLogin mfa={current.nextStep === 'Mfa'} />
      ) : current.nextStep === 'ConfirmEmail' && options.data.registrationEnabled ? (
        <GoogleEmailConfirmation />
      ) : (
        <>
          <p>{t('google.unknown')}</p>
          {options.data.registrationEnabled ? <TextLink to={paths.register}>{t('register.title')}</TextLink> : null}
          <TextLink to={paths.login}>{t('login.title')}</TextLink>
        </>
      )}
    </Page>
  )
}

const factorSchema = z.object({ twoFactorCode: totpCodeSchema.optional(), recoveryCode: recoveryCodeSchema.optional() })
function GoogleLogin({ mfa }: { readonly mfa: boolean }): React.JSX.Element {
  const { t } = useI18n()
  const transitions = useSessionTransitions()
  const fieldError = useValidationMessage()
  const [factor, setFactor] = useState<'authenticator' | 'recovery' | null>(mfa ? 'authenticator' : null)
  const form = useForm<z.input<typeof factorSchema>, unknown, z.output<typeof factorSchema>>({
    resolver: zodResolver(factorSchema),
    shouldUnregister: true,
  })
  useEffect(() => {
    if (factor) form.setFocus(factor === 'authenticator' ? 'twoFactorCode' : 'recoveryCode')
  }, [factor, form])
  const completion = usePublicMutation({
    gcTime: 0,
    mutationFn: (operation) => completeGoogle(operation, factorSchema.parse(form.getValues())),
    onSuccess: () => {
      form.reset()
      return transitions.signedIn(paths.account)
    },
    onError: (error) => {
      if (error instanceof ApiProblemError && error.problem.code === 'MFA_REQUIRED') setFactor('authenticator')
    },
  })
  return (
    <>
      {completion.isError ? (
        <RequestFailureAlert
          failure={classifyRequestFailure(completion.error)}
          problemMessages={{ MFA_REQUIRED: 'login.mfaRequired', INVALID_CREDENTIALS: 'google.invalidCode' }}
        />
      ) : null}
      <form
        method="post"
        noValidate
        className="flex flex-col gap-4"
        aria-busy={completion.isPending}
        onSubmit={(event) => {
          void runPublicOperation((operation) =>
            form.handleSubmit(async () => {
              if (!completion.isPending) await completion.mutateWithOperation(operation, undefined)
            })(event),
          ).catch(() => undefined)
        }}
      >
        {factor === 'authenticator' ? (
          <TextField
            label={t('mfa.code')}
            inputMode="numeric"
            autoComplete="one-time-code"
            error={fieldError(form.formState.errors.twoFactorCode?.message)}
            {...form.register('twoFactorCode')}
          />
        ) : null}
        {factor === 'recovery' ? (
          <TextField
            label={t('login.recoveryCode')}
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            error={fieldError(form.formState.errors.recoveryCode?.message)}
            {...form.register('recoveryCode')}
          />
        ) : null}
        {factor ? (
          <Button
            variant="neutral"
            onClick={() => {
              setFactor(factor === 'authenticator' ? 'recovery' : 'authenticator')
            }}
          >
            {t(factor === 'authenticator' ? 'login.useRecoveryCode' : 'login.useAuthenticatorCode')}
          </Button>
        ) : null}
        <SubmitButton pending={completion.isPending} label={t('google.signIn')} pendingLabel={t('login.submitting')} />
      </form>
    </>
  )
}
const confirmationSchema = z.object({
  code: z
    .string()
    .trim()
    .regex(/^\d{8}$/, 'validation.emailCodeInvalid'),
  password: currentPasswordSchema,
})
function GoogleEmailConfirmation(): React.JSX.Element {
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const [confirmed, setConfirmed] = useState(false)
  const form = useForm<z.input<typeof confirmationSchema>, unknown, z.output<typeof confirmationSchema>>({
    resolver: zodResolver(confirmationSchema),
    defaultValues: { code: '', password: '' },
  })
  const confirm = usePublicMutation({
    gcTime: 0,
    mutationFn: (operation) => {
      const value = confirmationSchema.parse(form.getValues())
      return confirmGoogleEmail(operation, value.code, value.password)
    },
    onSuccess: () => {
      form.reset()
      setConfirmed(true)
    },
  })
  if (confirmed)
    return (
      <>
        <Alert variant="success">{t('google.emailConfirmed')}</Alert>
        <GoogleAuthButton purpose="Login" />
      </>
    )
  return (
    <>
      <p>{t('google.confirmEmailIntro')}</p>
      {confirm.isError ? <RequestFailureAlert failure={classifyRequestFailure(confirm.error)} /> : null}
      <form
        noValidate
        method="post"
        className="flex flex-col gap-4"
        aria-busy={confirm.isPending}
        onSubmit={(event) => {
          void runPublicOperation((operation) =>
            form.handleSubmit(async () => {
              if (!confirm.isPending) await confirm.mutateWithOperation(operation, undefined)
            })(event),
          ).catch(() => undefined)
        }}
      >
        <TextField
          label={t('google.emailCode')}
          inputMode="numeric"
          autoComplete="one-time-code"
          error={fieldError(form.formState.errors.code?.message)}
          {...form.register('code')}
        />
        <PasswordField
          label={t('login.password')}
          autoComplete="current-password"
          error={fieldError(form.formState.errors.password?.message)}
          {...form.register('password')}
        />
        <SubmitButton
          pending={confirm.isPending}
          label={t('confirmEmail.submit')}
          pendingLabel={t('confirmEmail.submitting')}
        />
      </form>
    </>
  )
}
