import type { OperationHandle } from '@/shared/session-context'
import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { z } from 'zod'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { CheckboxField, PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { register } from './auth-api'
import {
  emailSchema,
  LEGAL_DOCUMENT_VERSIONS,
  REGISTRATION_DOCUMENTS,
  newPasswordSchema,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from './auth-schemas'
import { useValidationMessage, type ValidationKey } from './validation-message'

const registerSchema = z.object({
  email: emailSchema,
  password: newPasswordSchema,
  acceptTerms: z.boolean().refine((accepted) => accepted, 'validation.termsRequired' satisfies ValidationKey),
  acceptPrivacy: z.boolean().refine((accepted) => accepted, 'validation.privacyRequired' satisfies ValidationKey),
})

type RegisterForm = z.output<typeof registerSchema>

function submitRegistration(operation: OperationHandle, { email, password }: RegisterForm): Promise<void> {
  return register(operation, {
    email,
    password,
    acceptedTermsVersion: REGISTRATION_DOCUMENTS.terms.version,
    acceptedPrivacyVersion: REGISTRATION_DOCUMENTS.privacy.version,
    acceptedTermsLocale: REGISTRATION_DOCUMENTS.terms.locale,
    acceptedPrivacyLocale: REGISTRATION_DOCUMENTS.privacy.locale,
  })
}

export function RegisterPage(): React.JSX.Element {
  const navigate = useNavigate()
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const form = useForm<z.input<typeof registerSchema>, unknown, RegisterForm>({
    resolver: zodResolver(registerSchema),
    defaultValues: { email: '', password: '', acceptTerms: false, acceptPrivacy: false },
  })
  const registration = usePublicMutation({
    mutationFn: (operation) => submitRegistration(operation, registerSchema.parse(form.getValues())),
    onSuccess: () => navigate(paths.checkEmail),
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    runPublicOperation((operation) =>
      form.handleSubmit(async () => {
        if (!registration.isPending) await registration.mutateWithOperation(operation, undefined)
      })(event),
    )
  const { errors } = form.formState

  return (
    <Page title={t('register.title')} intro={t('register.intro')}>
      {registration.isError ? <RequestFailureAlert failure={classifyRequestFailure(registration.error)} /> : null}
      <form
        noValidate
        method="post"
        aria-busy={registration.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event).catch(() => undefined)
        }}
      >
        <TextField
          label={t('register.email')}
          type="email"
          inputMode="email"
          autoComplete="email"
          error={fieldError(errors.email?.message)}
          {...form.register('email')}
        />
        <PasswordField
          label={t('register.password')}
          hint={t('register.passwordHint', { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_LENGTH })}
          autoComplete="new-password"
          error={fieldError(errors.password?.message)}
          {...form.register('password')}
        />
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">{t('register.legalLegend')}</legend>
          <CheckboxField
            label={t('register.acceptTerms', { version: LEGAL_DOCUMENT_VERSIONS.terms })}
            error={fieldError(errors.acceptTerms?.message)}
            {...form.register('acceptTerms')}
          />
          <CheckboxField
            label={t('register.acceptPrivacy', { version: LEGAL_DOCUMENT_VERSIONS.privacy })}
            error={fieldError(errors.acceptPrivacy?.message)}
            {...form.register('acceptPrivacy')}
          />
        </fieldset>
        <SubmitButton
          pending={registration.isPending}
          label={t('register.submit')}
          pendingLabel={t('register.submitting')}
        />
      </form>
      <p className="text-text-muted">
        {t('register.haveAccount')} <TextLink to={paths.login}>{t('register.signInLink')}</TextLink>
      </p>
    </Page>
  )
}
