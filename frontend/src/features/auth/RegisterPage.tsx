import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { z } from 'zod'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { CheckboxField, PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { register } from './auth-api'
import {
  emailSchema,
  LEGAL_DOCUMENT_VERSIONS,
  newPasswordSchema,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from './auth-schemas'

const registerSchema = z.object({
  email: emailSchema,
  password: newPasswordSchema,
  acceptTerms: z.boolean().refine((accepted) => accepted, messages.validation.termsRequired),
  acceptPrivacy: z.boolean().refine((accepted) => accepted, messages.validation.privacyRequired),
})

type RegisterForm = z.output<typeof registerSchema>

function submitRegistration({ email, password }: RegisterForm): Promise<void> {
  return register({
    email,
    password,
    acceptedTermsVersion: LEGAL_DOCUMENT_VERSIONS.terms,
    acceptedPrivacyVersion: LEGAL_DOCUMENT_VERSIONS.privacy,
  })
}

export function RegisterPage(): React.JSX.Element {
  const navigate = useNavigate()
  const form = useForm<z.input<typeof registerSchema>, unknown, RegisterForm>({
    resolver: zodResolver(registerSchema),
    defaultValues: { email: '', password: '', acceptTerms: false, acceptPrivacy: false },
  })
  const registration = useMutation({
    mutationFn: submitRegistration,
    onSuccess: () => navigate(paths.checkEmail),
  })
  const submit = form.handleSubmit((values) => {
    if (!registration.isPending) {
      registration.mutate(values)
    }
  })
  const { errors } = form.formState

  return (
    <Page title={messages.register.title} intro={messages.register.intro}>
      {registration.isError ? <RequestFailureAlert failure={classifyRequestFailure(registration.error)} /> : null}
      <form
        noValidate
        method="post"
        aria-busy={registration.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <TextField
          label={messages.register.email}
          type="email"
          inputMode="email"
          autoComplete="email"
          error={errors.email?.message}
          {...form.register('email')}
        />
        <PasswordField
          label={messages.register.password}
          hint={messages.register.passwordHint(PASSWORD_MIN_LENGTH, PASSWORD_MAX_LENGTH)}
          autoComplete="new-password"
          error={errors.password?.message}
          {...form.register('password')}
        />
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">{messages.register.legalLegend}</legend>
          <CheckboxField
            label={messages.register.acceptTerms(LEGAL_DOCUMENT_VERSIONS.terms)}
            error={errors.acceptTerms?.message}
            {...form.register('acceptTerms')}
          />
          <CheckboxField
            label={messages.register.acceptPrivacy(LEGAL_DOCUMENT_VERSIONS.privacy)}
            error={errors.acceptPrivacy?.message}
            {...form.register('acceptPrivacy')}
          />
        </fieldset>
        <SubmitButton
          pending={registration.isPending}
          label={messages.register.submit}
          pendingLabel={messages.register.submitting}
        />
      </form>
      <p className="text-text-muted">
        {messages.register.haveAccount} <TextLink to={paths.login}>{messages.register.signInLink}</TextLink>
      </p>
    </Page>
  )
}
