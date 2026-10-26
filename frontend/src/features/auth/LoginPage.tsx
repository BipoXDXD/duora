import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useEffect, useState } from 'react'
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
import { Button } from '@/shared/ui/button'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { login } from './auth-api'
import { currentPasswordSchema, emailSchema, recoveryCodeSchema, totpCodeSchema } from './auth-schemas'
import { useValidationMessage } from './validation-message'

const loginSchema = z.object({
  email: emailSchema,
  password: currentPasswordSchema,
  // Só existem no formulário depois do MFA_REQUIRED, um de cada vez; o campo fora da tela não é registrado
  // (shouldUnregister) e fica undefined, então o corpo nunca leva os dois.
  twoFactorCode: totpCodeSchema.optional(),
  recoveryCode: recoveryCodeSchema.optional(),
})

type SecondFactor = 'authenticator' | 'recovery'

const SECOND_FACTOR_FIELDS = {
  authenticator: 'twoFactorCode',
  recovery: 'recoveryCode',
} as const satisfies Readonly<Record<SecondFactor, keyof z.input<typeof loginSchema>>>

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
    shouldUnregister: true,
  })
  const [secondFactor, setSecondFactor] = useState<SecondFactor | null>(null)
  // O campo do segundo fator aparece no lugar de onde a pessoa estava (o botão de enviar ou o de alternar): o foco
  // vai para ele, senão o leitor de tela só ouve o alerta e não acha onde digitar.
  useEffect(() => {
    if (secondFactor !== null) form.setFocus(SECOND_FACTOR_FIELDS[secondFactor])
  }, [secondFactor, form])
  const signIn = usePublicMutation({
    // Credenciais saem do formulário validado, nunca das variables do MutationCache (como na troca de senha).
    mutationFn: (operation) => login(operation, loginSchema.parse(form.getValues())),
    gcTime: 0,
    onError: (error) => {
      if (error instanceof ApiProblemError && error.problem.code === 'MFA_REQUIRED') {
        setSecondFactor((current) => current ?? 'authenticator')
      }
    },
    onSuccess: () => transitions.signedIn(from ?? paths.account),
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    runPublicOperation((operation) =>
      form.handleSubmit(async () => {
        if (!signIn.isPending) await signIn.mutateWithOperation(operation, undefined)
      })(event),
    )
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
          void submit(event).catch(() => undefined)
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
        {secondFactor === 'authenticator' ? (
          <TextField
            label={t('mfa.code')}
            inputMode="numeric"
            autoComplete="one-time-code"
            error={fieldError(errors.twoFactorCode?.message)}
            {...form.register('twoFactorCode')}
          />
        ) : null}
        {secondFactor === 'recovery' ? (
          <TextField
            label={t('login.recoveryCode')}
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            error={fieldError(errors.recoveryCode?.message)}
            {...form.register('recoveryCode')}
          />
        ) : null}
        {secondFactor !== null ? (
          <Button
            variant="neutral"
            className="self-start"
            onClick={() => {
              setSecondFactor(secondFactor === 'authenticator' ? 'recovery' : 'authenticator')
            }}
          >
            {secondFactor === 'authenticator' ? t('login.useRecoveryCode') : t('login.useAuthenticatorCode')}
          </Button>
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
