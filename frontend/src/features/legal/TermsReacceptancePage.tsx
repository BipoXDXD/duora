import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { useValidationMessage, type ValidationKey } from '@/features/auth/validation-message'
import { useAccount } from '@/features/session/account-context'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { usePrivateOperation } from '@/features/session/private-operation'
import { meQueryKey } from '@/features/session/query-keys'
import type { Me } from '@/features/session/session-api'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { LOCALE_AUTONYMS, SUPPORTED_LOCALES, type Locale } from '@/shared/i18n/locale'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { CheckboxField, SelectField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { legalDocumentPath, registrationReceipt, useLegalPublication, type LegalDocument } from './legal-publication'
import { acceptCurrentTerms } from './terms-acceptance-api'

const schema = z.object({
  acceptTerms: z.boolean().refine((accepted) => accepted, 'validation.termsAcceptanceRequired' satisfies ValidationKey),
  confirmAdult: z
    .boolean()
    .refine((confirmed) => confirmed, 'validation.adultDeclarationRequired' satisfies ValidationKey),
})

/**
 * Novo aceite dos Termos ativos (issue #296, API-A17). Não bloqueia o app: a pessoa chega aqui pelo aviso e pode
 * voltar sem aceitar. Depois do aceite, a conta em cache passa a estar em dia e o aviso some.
 */
export function TermsReacceptancePage(): React.JSX.Element {
  const account = useAccount()
  const { t, locale } = useI18n()
  const publication = useLegalPublication()
  const [termsLocale, setTermsLocale] = useState<Locale>(locale)
  const [justAccepted, setJustAccepted] = useState(false)
  // A Política só entra no recibo do cadastro; aqui o recibo é o dos Termos ativos no idioma escolhido.
  const terms = publication.data
    ? (registrationReceipt(publication.data, termsLocale, termsLocale)?.terms ?? null)
    : null
  if (account.currentTermsAccepted) {
    return (
      <Page title={t('termsReacceptance.title')}>
        <Alert variant="success" focusOnMount={justAccepted}>
          {t(justAccepted ? 'termsReacceptance.accepted' : 'termsReacceptance.upToDate')}
        </Alert>
        <TextLink to={paths.account}>{t('termsReacceptance.backToAccount')}</TextLink>
      </Page>
    )
  }
  return (
    <Page title={t('termsReacceptance.title')} intro={t('termsReacceptance.intro')}>
      {publication.isPending ? (
        <p role="status">{t('legal.loading')}</p>
      ) : terms === null ? (
        <Alert variant="error">{t('termsReacceptance.unavailable')}</Alert>
      ) : (
        <>
          <SelectField
            label={t('legal.termsLanguage')}
            value={termsLocale}
            options={SUPPORTED_LOCALES.map((documentLocale) => ({
              value: documentLocale,
              label: LOCALE_AUTONYMS[documentLocale],
            }))}
            onChange={(event) => {
              const chosen = SUPPORTED_LOCALES.find((candidate) => candidate === event.target.value)
              if (chosen !== undefined) setTermsLocale(chosen)
            }}
          />
          <p>
            <TextLink to={legalDocumentPath(terms)} lang={terms.locale}>
              {t('termsReacceptance.readTerms', { title: terms.title, version: terms.version })}
            </TextLink>
          </p>
          <TermsReacceptanceForm
            key={`${terms.version}/${terms.locale}/${terms.sha256}`}
            terms={terms}
            onAccepted={() => {
              setJustAccepted(true)
            }}
          />
        </>
      )}
      <TextLink to={paths.account}>{t('termsReacceptance.later')}</TextLink>
    </Page>
  )
}

function TermsReacceptanceForm({
  terms,
  onAccepted,
}: {
  readonly terms: LegalDocument
  readonly onAccepted: () => void
}): React.JSX.Element {
  const { t } = useI18n()
  const account = useAccount()
  const queries = useQueryClient()
  const privateOperation = usePrivateOperation()
  const fieldError = useValidationMessage()
  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: { acceptTerms: false, confirmAdult: false },
  })
  const accept = usePrivateMutation({
    mutationFn: (operation) => acceptCurrentTerms(operation, terms),
    onSuccess: () => {
      if (queries.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      onAccepted()
      queries.setQueryData<Me>(meQueryKey, (current) =>
        current === undefined ? current : { ...current, currentTermsAccepted: true },
      )
    },
  })
  const submit = (event: React.BaseSyntheticEvent) =>
    privateOperation.run((operation) =>
      form.handleSubmit(async () => {
        if (!accept.isPending) await accept.mutateWithOperation(operation, undefined)
      })(event),
    )
  const { errors } = form.formState
  return (
    <>
      {accept.isError ? (
        <RequestFailureAlert
          failure={classifyRequestFailure(accept.error)}
          problemMessages={{ VALIDATION_FAILED: 'termsReacceptance.outdated' }}
        />
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={accept.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event).catch(() => undefined)
        }}
      >
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">{t('termsReacceptance.legend')}</legend>
          <CheckboxField
            label={t('termsReacceptance.acceptTerms', { version: terms.version })}
            error={fieldError(errors.acceptTerms?.message)}
            {...form.register('acceptTerms')}
          />
          <CheckboxField
            label={t('termsReacceptance.confirmAdult')}
            error={fieldError(errors.confirmAdult?.message)}
            {...form.register('confirmAdult')}
          />
        </fieldset>
        <SubmitButton
          pending={accept.isPending}
          label={t('termsReacceptance.submit')}
          pendingLabel={t('termsReacceptance.submitting')}
        />
      </form>
    </>
  )
}
