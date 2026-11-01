import { useEffect, useId, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useAccount } from '@/features/session/account-context'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { isMfaOptional } from '@/features/session/mfa-policy'
import { disableMfa } from '@/features/auth/auth-api'
import { executeSecurityAction } from '@/features/auth/security-action'
import { useOwnedSecurityResult } from '@/features/auth/security-result-hooks'
import { SignInAgainButton } from '@/features/auth/SignInAgainButton'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { TextLink } from '@/shared/ui/page'

export function AccountMfaSettings(): React.JSX.Element {
  const account = useAccount()
  const { t } = useI18n()
  const cache = useQueryClient()
  const result = useOwnedSecurityResult()
  const optional = isMfaOptional(account.roles)
  const [confirming, setConfirming] = useState(false)
  const headingId = useId(),
    confirmationId = useId(),
    warningId = useId()
  const group = useRef<HTMLDivElement>(null)
  const opener = useRef<HTMLButtonElement>(null)
  const restoreFocus = useRef(false)
  useEffect(() => {
    if (confirming) group.current?.focus()
    else if (restoreFocus.current) {
      restoreFocus.current = false
      opener.current?.focus()
    }
  }, [confirming])
  const disable = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      executeSecurityAction(
        operation,
        async () => {
          await disableMfa(operation)
          return { kind: 'mfa-disabled' }
        },
        cache,
      ),
  })
  const failure = disable.error === null ? null : classifyRequestFailure(disable.error)
  const needsSignIn = failure?.kind === 'problem' && failure.code === 'REAUTHENTICATION_REQUIRED'

  return (
    <section
      aria-labelledby={headingId}
      className="flex flex-col items-start gap-4 rounded-md border border-border p-4"
    >
      <h2 id={headingId} className="text-xl font-semibold">
        {t('mfaSettings.title')}
      </h2>
      <p>{t(account.mfaEnabled ? 'mfaSettings.on' : 'mfaSettings.off')}</p>
      <p>{t(optional ? 'mfaSettings.optional' : 'mfaSettings.required')}</p>
      {result?.kind === 'mfa-disabled' ? (
        <Alert variant="success" focusOnMount>
          {t('mfaSettings.disabled')}
        </Alert>
      ) : null}
      {failure === null ? null : (
        <RequestFailureAlert
          failure={failure}
          problemMessages={{
            REAUTHENTICATION_REQUIRED: 'changePassword.reauthenticate',
            CSRF_VALIDATION_FAILED: 'changePassword.csrf',
            MFA_REQUIRED_BY_ROLE: 'mfaSettings.required',
          }}
        />
      )}
      {!optional ? null : !account.mfaEnabled ? (
        <TextLink to={paths.mfaEnrollment}>{t('mfaSettings.enable')}</TextLink>
      ) : confirming ? (
        <div
          ref={group}
          role="group"
          aria-labelledby={confirmationId}
          aria-describedby={warningId}
          tabIndex={-1}
          className="flex flex-col items-start gap-4"
        >
          <h3 id={confirmationId} className="font-semibold">
            {t('mfaSettings.confirmTitle')}
          </h3>
          <p id={warningId}>{t('mfaSettings.warning')}</p>
          <div className="flex flex-wrap gap-2">
            <Button
              variant="destructive"
              aria-disabled={disable.isPending}
              onClick={() => {
                if (!disable.isPending) disable.mutate()
              }}
            >
              {t(disable.isPending ? 'mfaSettings.disabling' : 'mfaSettings.confirm')}
            </Button>
            <Button
              variant="secondary"
              aria-disabled={disable.isPending}
              onClick={() => {
                if (!disable.isPending) {
                  restoreFocus.current = true
                  setConfirming(false)
                }
              }}
            >
              {t('mfaSettings.cancel')}
            </Button>
          </div>
        </div>
      ) : (
        <Button
          ref={opener}
          variant="secondary"
          onClick={() => {
            setConfirming(true)
          }}
        >
          {t('mfaSettings.disable')}
        </Button>
      )}
      {needsSignIn ? <SignInAgainButton /> : null}
    </section>
  )
}
