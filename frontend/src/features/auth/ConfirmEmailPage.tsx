import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { classifyRequestFailure, type RequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { PasswordField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { confirmEmail } from './auth-api'
import { currentPasswordSchema, type EmailLink } from './auth-schemas'
import { useEmailLinkFromUrl } from './use-email-link'
import { useValidationMessage } from './validation-message'

const PROBLEM_MESSAGES = { INVALID_TOKEN: 'confirmEmail.invalidToken' } as const

const confirmEmailSchema = z.object({ password: currentPasswordSchema })

/**
 * Confirmação de e-mail pelo link recebido. Pede um clique em vez de confirmar ao abrir a página
 * (scanners de link que executam JavaScript não confirmam a conta sozinhos, API-A03) e a senha do
 * cadastro (um link gerado por terceiro não confirma a conta na caixa de outra pessoa).
 */
export function ConfirmEmailPage(): React.JSX.Element {
  const link = useEmailLinkFromUrl()
  const { t } = useI18n()

  return (
    <Page title={t('confirmEmail.title')}>
      {link === null ? (
        <>
          <Alert variant="error">{t('confirmEmail.invalidLink')}</Alert>
          <p>
            <TextLink to={paths.login}>{t('confirmEmail.signInLink')}</TextLink>
          </p>
        </>
      ) : (
        <ConfirmEmailForm link={link} />
      )}
    </Page>
  )
}

function ConfirmEmailForm({ link }: { readonly link: EmailLink }): React.JSX.Element {
  const { t } = useI18n()
  const fieldError = useValidationMessage()
  const form = useForm<z.input<typeof confirmEmailSchema>, unknown, z.output<typeof confirmEmailSchema>>({
    resolver: zodResolver(confirmEmailSchema),
    defaultValues: { password: '' },
  })
  const confirmation = useMutation({ mutationFn: (password: string) => confirmEmail(link, password) })
  const submit = form.handleSubmit(({ password }) => {
    if (!confirmation.isPending) {
      confirmation.mutate(password)
    }
  })

  if (confirmation.isSuccess) {
    return (
      <>
        <Alert variant="success" focusOnMount>
          {t('confirmEmail.success')}
        </Alert>
        <p>
          <TextLink to={paths.login}>{t('confirmEmail.signInLink')}</TextLink>
        </p>
      </>
    )
  }
  const failure = confirmation.isError ? classifyRequestFailure(confirmation.error) : null
  return (
    <>
      {failure === null ? null : <RequestFailureAlert failure={failure} problemMessages={PROBLEM_MESSAGES} />}
      {isInvalidToken(failure) ? (
        <p>
          <TextLink to={paths.register}>{t('confirmEmail.registerAgain')}</TextLink>
        </p>
      ) : null}
      <p className="text-text-muted">{t('confirmEmail.intro')}</p>
      <form
        noValidate
        method="post"
        aria-busy={confirmation.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <PasswordField
          label={t('confirmEmail.password')}
          hint={t('confirmEmail.passwordHint')}
          autoComplete="current-password"
          error={fieldError(form.formState.errors.password?.message)}
          {...form.register('password')}
        />
        <SubmitButton
          pending={confirmation.isPending}
          label={t('confirmEmail.submit')}
          pendingLabel={t('confirmEmail.submitting')}
        />
      </form>
    </>
  )
}

function isInvalidToken(failure: RequestFailure | null): boolean {
  return failure?.kind === 'problem' && failure.code === 'INVALID_TOKEN'
}
