import { usePrivateMutation } from '@/features/session/private-mutation'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { useOwnedSecurityResult, useSecurityResultSnapshot } from './security-result-hooks'
import { pageSecurityResult } from './page-security-result'
import { executeSecurityAction } from './security-action'
import { SecurityCompletionNotice } from './SecurityCompletionNotice'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton } from '@/shared/ui/page'
import { confirmMfa, startMfaEnrollment } from './auth-api'
import { totpCodeSchema } from './auth-schemas'
import { SignInAgainButton } from './SignInAgainButton'

/** Segredo e códigos só em memória local; nunca no cache de consultas, URL ou armazenamento. */
export function MfaEnrollmentPage(): React.JSX.Element {
  const { t } = useI18n()
  const navigate = useNavigate()
  const cache = useQueryClient()
  const result = useOwnedSecurityResult()
  const resultState = useSecurityResultSnapshot()
  const sharedKey = result?.kind === 'mfa-enrollment' ? result.sharedKey : null
  const recoveryCodes = result?.kind === 'recovery-codes' ? result.codes : null
  const [code, setCode] = useState('')
  const [invalid, setInvalid] = useState(false)
  const [rejections, setRejections] = useState(0)
  const codeInput = useRef<HTMLInputElement>(null)
  // O erro do campo não é região viva: o foco nele, depois de renderizar a descrição, faz o leitor de tela ler o erro.
  useEffect(() => {
    if (rejections > 0) codeInput.current?.focus()
  }, [rejections])
  const start = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      executeSecurityAction(
        operation,
        async () => ({ kind: 'mfa-enrollment', ...(await startMfaEnrollment(operation)) }),
        cache,
      ),
  })
  const confirm = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      executeSecurityAction(
        operation,
        async () => {
          const received = await confirmMfa(operation, totpCodeSchema.parse(code))
          return { kind: 'recovery-codes', codes: received.recoveryCodes, resultSessionId: received.resultSessionId }
        },
        cache,
      ),
  })
  if (resultState.phase === 'pending')
    return (
      <Page title={t('mfa.title')}>
        <p role="status">{t('account.loading')}</p>
      </Page>
    )
  if (recoveryCodes !== null)
    return (
      <Page title={t('mfa.recoveryTitle')}>
        {/* O formulário some ao confirmar: o anúncio recebe o foco para ele não cair no body. */}
        <Alert variant="success" focusOnMount>
          {t('mfa.enabled')}
        </Alert>
        <p>{t('mfa.recoveryInstructions')}</p>
        <ul>
          {recoveryCodes.map((value) => (
            <li key={value}>
              <code>{value}</code>
            </li>
          ))}
        </ul>
        <Button
          onClick={() => {
            pageSecurityResult.discard()
            void navigate(paths.account, { replace: true })
          }}
        >
          {t('mfa.saved')}
        </Button>
      </Page>
    )
  const error = confirm.error ?? start.error
  return (
    <Page title={t('mfa.title')}>
      <p>{t('mfa.instructions')}</p>
      <SecurityCompletionNotice />
      {error !== null ? (
        <RequestFailureAlert
          failure={classifyRequestFailure(error)}
          problemMessages={{
            MFA_CODE_INVALID: 'mfa.invalidCode',
            REAUTHENTICATION_REQUIRED: 'changePassword.reauthenticate',
            CSRF_VALIDATION_FAILED: 'changePassword.csrf',
            MFA_ALREADY_ENABLED: 'mfa.alreadyEnabled',
            MFA_ENROLLMENT_NOT_STARTED: 'mfa.notStarted',
          }}
        />
      ) : null}
      {sharedKey === null ? (
        <Button
          aria-disabled={start.isPending}
          onClick={() => {
            if (!start.isPending) start.mutate()
          }}
        >
          {start.isPending ? t('mfa.starting') : t('mfa.start')}
        </Button>
      ) : (
        <FocusOnMount>
          <p>
            {t('mfa.key')} <code className="break-all select-all">{sharedKey}</code>
          </p>
          <form
            noValidate
            method="post"
            className="flex flex-col gap-6"
            aria-busy={confirm.isPending}
            onSubmit={(event) => {
              event.preventDefault()
              const valid = totpCodeSchema.safeParse(code).success
              setInvalid(!valid)
              if (!valid) setRejections((count) => count + 1)
              if (valid && !confirm.isPending) confirm.mutate()
            }}
          >
            <TextField
              label={t('mfa.code')}
              ref={codeInput}
              name="code"
              inputMode="numeric"
              autoComplete="one-time-code"
              value={code}
              error={invalid ? t('mfa.invalidCode') : undefined}
              onChange={(event) => {
                setCode(event.target.value)
              }}
            />
            <SubmitButton pending={confirm.isPending} label={t('mfa.confirm')} pendingLabel={t('mfa.confirming')} />
          </form>
        </FocusOnMount>
      )}
      <SignInAgainButton />
    </Page>
  )
}

/** Bloco que substitui o controle focado (o botão de início some): recebe o foco ao aparecer. */
function FocusOnMount({ children }: { readonly children: ReactNode }): React.JSX.Element {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    ref.current?.focus()
  }, [])
  return (
    <div ref={ref} tabIndex={-1} className="flex flex-col gap-6">
      {children}
    </div>
  )
}
