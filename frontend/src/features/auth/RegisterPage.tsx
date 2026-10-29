import { useEffect, useRef, useState } from 'react'
import { LOCALE_AUTONYMS, SUPPORTED_LOCALES, type Locale } from '@/shared/i18n/locale'
import { LegalNavigation } from '@/features/legal/LegalNavigation'
import { legalCopy } from '@/features/legal/legal-copy'
import {
  legalDocumentPath,
  registrationReceipt,
  useLegalPublication,
  type LegalReceipt,
} from '@/features/legal/legal-publication'
import type { OperationHandle } from '@/shared/session-context'
import { usePublicMutation, runPublicOperation } from '@/features/session/public-operation'
import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { z } from 'zod'
import { apiClient, classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { CheckboxField, PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { register } from './auth-api'
import { emailSchema, newPasswordSchema, PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH } from './auth-schemas'
import { useValidationMessage, type ValidationKey } from './validation-message'

const registerSchema = z.object({
  email: emailSchema,
  password: newPasswordSchema,
  acceptTerms: z.boolean().refine((accepted) => accepted, 'validation.termsRequired' satisfies ValidationKey),
  acceptPrivacy: z.boolean().refine((accepted) => accepted, 'validation.privacyRequired' satisfies ValidationKey),
  confirmAdult: z.boolean().refine((confirmed) => confirmed, 'validation.adultRequired' satisfies ValidationKey),
})

type RegisterForm = z.output<typeof registerSchema>

function submitRegistration(
  operation: OperationHandle,
  { email, password }: RegisterForm,
  receipt: LegalReceipt,
): Promise<void> {
  return register(operation, {
    email,
    password,
    acceptedTermsSha256: receipt.terms.sha256,
    acceptedPrivacySha256: receipt.privacy.sha256,
    acceptedTermsVersion: receipt.terms.version,
    acceptedPrivacyVersion: receipt.privacy.version,
    acceptedTermsLocale: receipt.terms.locale,
    acceptedPrivacyLocale: receipt.privacy.locale,
    // The schema refinement only lets a checked box through; the literal keeps the contract explicit.
    confirmedAdult: true,
  })
}

export function RegisterPage(): React.JSX.Element {
  const { locale, t } = useI18n()
  const copy = legalCopy(t)
  const publication = useLegalPublication()
  const [termsLocale, setTermsLocale] = useState<Locale>(locale)
  const [privacyLocale, setPrivacyLocale] = useState<Locale>(locale)
  const receipt = publication.data ? registrationReceipt(publication.data, termsLocale, privacyLocale) : null
  return (
    <Page title={t('register.title')} intro={t('register.intro')}>
      <LegalNavigation />
      {publication.isPending ? (
        <p role="status">{copy.loading}</p>
      ) : !receipt ? (
        <p role="alert">{copy.unavailable}</p>
      ) : (
        <>
          <label>
            {copy.termsLanguage}
            <select
              className="min-h-11 rounded-md border border-border bg-surface px-3"
              value={termsLocale}
              onChange={(event) => {
                setTermsLocale(event.target.value as Locale)
              }}
            >
              {SUPPORTED_LOCALES.map((documentLocale) => (
                <option key={documentLocale} value={documentLocale} lang={documentLocale}>
                  {LOCALE_AUTONYMS[documentLocale]}
                </option>
              ))}
            </select>
          </label>
          <label>
            {copy.privacyLanguage}
            <select
              className="min-h-11 rounded-md border border-border bg-surface px-3"
              value={privacyLocale}
              onChange={(event) => {
                setPrivacyLocale(event.target.value as Locale)
              }}
            >
              {SUPPORTED_LOCALES.map((documentLocale) => (
                <option key={documentLocale} value={documentLocale} lang={documentLocale}>
                  {LOCALE_AUTONYMS[documentLocale]}
                </option>
              ))}
            </select>
          </label>
          <p>{copy.acknowledgement}</p>
          <nav aria-label={copy.navigation} className="flex flex-wrap gap-4">
            <TextLink to={legalDocumentPath(receipt.terms)} lang={receipt.terms.locale}>
              {receipt.terms.title} · {receipt.terms.version} · {receipt.terms.locale}
            </TextLink>
            <TextLink to={legalDocumentPath(receipt.privacy)} lang={receipt.privacy.locale}>
              {receipt.privacy.title} · {receipt.privacy.version} · {receipt.privacy.locale}
            </TextLink>
          </nav>
          <RegistrationForm
            key={`${receipt.terms.version}/${receipt.terms.locale}/${receipt.terms.sha256}/${receipt.privacy.version}/${receipt.privacy.locale}/${receipt.privacy.sha256}`}
            receipt={receipt}
          />
        </>
      )}
      <p className="text-text-muted">
        {t('register.haveAccount')} <TextLink to={paths.login}>{t('register.signInLink')}</TextLink>
      </p>
    </Page>
  )
}

function RegistrationForm({ receipt }: { readonly receipt: LegalReceipt }): React.JSX.Element {
  const navigate = useNavigate()
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const mounted = useRef(true)
  const pendingOperations = useRef(new Set<OperationHandle>())
  useEffect(() => {
    mounted.current = true
    const pending = pendingOperations.current
    return () => {
      mounted.current = false
      for (const operation of pending) apiClient.sessionContext.finish(operation)
      pending.clear()
    }
  }, [])
  const form = useForm<z.input<typeof registerSchema>, unknown, RegisterForm>({
    resolver: zodResolver(registerSchema),
    defaultValues: { email: '', password: '', acceptTerms: false, acceptPrivacy: false, confirmAdult: false },
  })
  const registration = usePublicMutation({
    mutationFn: (operation) => {
      if (!mounted.current) throw new Error('Registration documents changed')
      return submitRegistration(operation, registerSchema.parse(form.getValues()), receipt)
    },
    onSuccess: () => {
      if (mounted.current) void navigate(paths.checkEmail)
    },
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    runPublicOperation(async (operation) => {
      pendingOperations.current.add(operation)
      try {
        await form.handleSubmit(async () => {
          if (mounted.current && !registration.isPending) await registration.mutateWithOperation(operation, undefined)
        })(event)
      } finally {
        pendingOperations.current.delete(operation)
      }
    })
  const { errors } = form.formState

  return (
    <>
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
            label={t('register.acceptTerms', { version: receipt.terms.version })}
            error={fieldError(errors.acceptTerms?.message)}
            {...form.register('acceptTerms')}
          />
          <CheckboxField
            label={t('register.acceptPrivacy', { version: receipt.privacy.version })}
            error={fieldError(errors.acceptPrivacy?.message)}
            {...form.register('acceptPrivacy')}
          />
          <CheckboxField
            label={t('register.confirmAdult')}
            error={fieldError(errors.confirmAdult?.message)}
            {...form.register('confirmAdult')}
          />
        </fieldset>
        <SubmitButton
          pending={registration.isPending}
          label={t('register.submit')}
          pendingLabel={t('register.submitting')}
        />
      </form>
    </>
  )
}
