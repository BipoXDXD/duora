import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useSessionTransitions } from '@/features/session/session-hooks'
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
  const transitions = useSessionTransitions()
  const [sharedKey, setSharedKey] = useState<string | null>(null)
  const [recoveryCodes, setRecoveryCodes] = useState<readonly string[] | null>(null)
  const [code, setCode] = useState('')
  const [invalid, setInvalid] = useState(false)
  const start = useMutation({
    gcTime: 0,
    mutationFn: async () => {
      const enrollment = await startMfaEnrollment()
      setSharedKey(enrollment.sharedKey)
    },
  })
  const confirm = useMutation({
    gcTime: 0,
    mutationFn: async () => {
      const codes = await confirmMfa(code)
      setSharedKey(null)
      setCode('')
      setRecoveryCodes(codes)
    },
  })
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
            setRecoveryCodes(null)
            void transitions.signedIn(paths.account)
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
              if (valid && !confirm.isPending) confirm.mutate()
            }}
          >
            <TextField
              label={t('mfa.code')}
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
