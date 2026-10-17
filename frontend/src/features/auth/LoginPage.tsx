import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { useLocation } from 'react-router'
import { z } from 'zod'
import { readLoginState, type LoginNotice } from '@/features/session/login-state'
import { useDiscardCachedIdentity, useSessionTransitions } from '@/features/session/session-hooks'
import { ApiProblemError, classifyRequestFailure } from '@/shared/api'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { login } from './auth-api'
import { currentPasswordSchema, emailSchema } from './auth-schemas'
import { useValidationMessage } from './validation-message'

const loginSchema = z.object({
  email: emailSchema,
  password: currentPasswordSchema,
  twoFactorCode: z.string().optional(),
})

const NOTICE_MESSAGES: Readonly<Record<LoginNotice, PlainMessageKey>> = {
  'session-expired': 'login.sessionExpired',
  'sign-in-required': 'login.signInRequired',
  'signed-out': 'login.signedOut',
}

const PROBLEM_MESSAGES = {
  INVALID_CREDENTIALS: 'login.invalidCredentials',
  MFA_REQUIRED: 'login.mfaRequired',
} as const satisfies Readonly<Record<string, PlainMessageKey>>

export function LoginPage(): React.JSX.Element {
  useDiscardCachedIdentity()
  const { notice, from } = readLoginState(useLocation().state)
  const transitions = useSessionTransitions()
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const form = useForm<z.input<typeof loginSchema>, unknown, z.output<typeof loginSchema>>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
  })
  const signIn = useMutation({
    mutationFn: login,
    onSuccess: () => transitions.signedIn(from ?? paths.account),
  })
  const submit = form.handleSubmit((credentials) => {
    if (!signIn.isPending) {
      signIn.mutate(credentials)
    }
  })
  const { errors } = form.formState

  return (
    <Page title={t('login.title')}>
      {notice !== undefined && signIn.isIdle ? <Alert variant="info">{t(NOTICE_MESSAGES[notice])}</Alert> : null}
      {signIn.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(signIn.error)} problemMessages={PROBLEM_MESSAGES} />
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={signIn.isPending}
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
          error={fieldError(errors.email?.message)}
          {...form.register('email')}
        />
        <PasswordField
          label={t('login.password')}
          autoComplete="current-password"
          error={fieldError(errors.password?.message)}
          {...form.register('password')}
        />
        {(signIn.error instanceof ApiProblemError && signIn.error.problem.code === 'MFA_REQUIRED') ||
        form.getValues('twoFactorCode') !== undefined ? (
          <TextField
            label={t('mfa.code')}
            inputMode="numeric"
            autoComplete="one-time-code"
            {...form.register('twoFactorCode')}
          />
        ) : null}
        <SubmitButton pending={signIn.isPending} label={t('login.submit')} pendingLabel={t('login.submitting')} />
      </form>
      <nav aria-label={t('login.otherPaths')} className="flex flex-col items-start gap-2">
        <TextLink to={paths.forgotPassword}>{t('login.forgotPasswordLink')}</TextLink>
        <p className="text-text-muted">
          {t('login.noAccount')} <TextLink to={paths.register}>{t('login.registerLink')}</TextLink>
        </p>
      </nav>
    </Page>
  )
}
