import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { useLocation } from 'react-router'
import { z } from 'zod'
import { readLoginState, type LoginNotice } from '@/features/session/login-state'
import { useDiscardCachedIdentity, useSessionTransitions } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { login } from './auth-api'
import { currentPasswordSchema, emailSchema } from './auth-schemas'

const loginSchema = z.object({ email: emailSchema, password: currentPasswordSchema })

const NOTICE_MESSAGES: Readonly<Record<LoginNotice, string>> = {
  'session-expired': messages.login.sessionExpired,
  'sign-in-required': messages.login.signInRequired,
  'signed-out': messages.login.signedOut,
}

const PROBLEM_MESSAGES = {
  INVALID_CREDENTIALS: messages.login.invalidCredentials,
  MFA_REQUIRED: messages.login.mfaRequired,
}

export function LoginPage(): React.JSX.Element {
  useDiscardCachedIdentity()
  const { notice, from } = readLoginState(useLocation().state)
  const transitions = useSessionTransitions()
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
    <Page title={messages.login.title}>
      {notice !== undefined && signIn.isIdle ? <Alert variant="info">{NOTICE_MESSAGES[notice]}</Alert> : null}
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
          label={messages.login.email}
          type="email"
          inputMode="email"
          autoComplete="email"
          error={errors.email?.message}
          {...form.register('email')}
        />
        <PasswordField
          label={messages.login.password}
          autoComplete="current-password"
          error={errors.password?.message}
          {...form.register('password')}
        />
        <SubmitButton
          pending={signIn.isPending}
          label={messages.login.submit}
          pendingLabel={messages.login.submitting}
        />
      </form>
      <nav aria-label={messages.login.otherPaths} className="flex flex-col items-start gap-2">
        <TextLink to={paths.forgotPassword}>{messages.login.forgotPasswordLink}</TextLink>
        <p className="text-text-muted">
          {messages.login.noAccount} <TextLink to={paths.register}>{messages.login.registerLink}</TextLink>
        </p>
      </nav>
    </Page>
  )
}
