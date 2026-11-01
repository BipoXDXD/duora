import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useAccount } from '@/features/session/account-context'
import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { executeSecurityAction } from '@/features/auth/security-action'
import { useOwnedSecurityResult } from '@/features/auth/security-result-hooks'
import {
  confirmGoogleLink,
  fetchExternalLogins,
  reauthenticateGoogleSettings,
  removeGoogleLink,
  requestGoogle,
  useGoogleFlow,
  useGoogleOptions,
  useGoogleFlowExpired,
} from '@/features/auth/google-api'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { PasswordField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'

export function GoogleAccountSettings(): React.JSX.Element {
  const account = useAccount()
  const options = useGoogleOptions()
  const canLink =
    options.data?.enabled === true &&
    options.data.linkingEnabled &&
    account.roles.length === 1 &&
    account.roles[0] === 'participant' &&
    account.status !== 'Suspended'
  return <GoogleSettings canLink={canLink} />
}
function GoogleSettings({ canLink }: { readonly canLink: boolean }): React.JSX.Element | null {
  const { t } = useI18n()
  const account = useAccount()
  const privateOperation = usePrivateOperation()
  const cache = useQueryClient()
  const result = useOwnedSecurityResult()
  const [confirming, setConfirming] = useState(false)
  const [reauthenticated, setReauthenticated] = useState(false)
  const logins = useQuery({
    queryKey: ['external-logins', account.id],
    gcTime: 0,
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchExternalLogins(operation, signal)),
  })
  const request = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) => requestGoogle(operation, 'Link'),
    onSuccess: (url) => {
      window.location.assign(url)
    },
  })
  const remove = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      executeSecurityAction(
        operation,
        async () => {
          await removeGoogleLink(operation)
          return { kind: 'external-login-unlinked' }
        },
        cache,
      ),
  })
  const error = request.error ?? remove.error
  const failure = error ? classifyRequestFailure(error) : null
  const needsReauth = failure?.kind === 'problem' && failure.code === 'REAUTHENTICATION_REQUIRED'
  const linked = (logins.data?.length ?? 0) > 0
  if (!canLink && !linked && result?.kind !== 'external-login-unlinked' && !logins.isError) return null
  return (
    <section
      aria-label={t('google.settings')}
      className="flex flex-col items-start gap-4 rounded-md border border-border p-4"
    >
      <h2 className="text-xl font-semibold">{t('google.settings')}</h2>
      <p>{t('google.localPassword')}</p>
      {result?.kind === 'external-login-unlinked' ? (
        <Alert variant="success" focusOnMount>
          {t('google.removed')}
        </Alert>
      ) : null}
      {reauthenticated ? <Alert variant="success">{t('google.reauthenticated')}</Alert> : null}
      {failure ? (
        <RequestFailureAlert
          failure={failure}
          problemMessages={{ REAUTHENTICATION_REQUIRED: 'google.reauthRequired' }}
        />
      ) : null}
      {logins.isError ? <RequestFailureAlert failure={classifyRequestFailure(logins.error)} /> : null}
      {logins.isPending ? (
        <p role="status">{t('account.loading')}</p>
      ) : logins.data ? (
        linked ? (
          <>
            <p>{t('google.linked')}</p>
            {!confirming ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setConfirming(true)
                }}
              >
                {t('google.remove')}
              </Button>
            ) : (
              <>
                <p>{t('google.removeWarning')}</p>
                <Button
                  variant="destructive"
                  aria-disabled={remove.isPending || needsReauth}
                  onClick={() => {
                    if (!remove.isPending && !needsReauth) remove.mutate()
                  }}
                >
                  {t('google.confirmRemove')}
                </Button>
                <Button
                  variant="neutral"
                  aria-disabled={remove.isPending}
                  onClick={() => {
                    if (!remove.isPending) setConfirming(false)
                  }}
                >
                  {t('google.cancel')}
                </Button>
              </>
            )}
          </>
        ) : canLink ? (
          <Button
            variant="secondary"
            aria-disabled={request.isPending || needsReauth}
            onClick={() => {
              if (!request.isPending && !needsReauth) request.mutate()
            }}
          >
            {t('google.link')}
          </Button>
        ) : null
      ) : null}
      {needsReauth ? (
        <GoogleLocalReauthentication
          onConfirmed={() => {
            request.reset()
            remove.reset()
            setReauthenticated(true)
          }}
        />
      ) : null}
    </section>
  )
}

function GoogleLocalReauthentication({ onConfirmed }: { readonly onConfirmed: () => void }): React.JSX.Element {
  const { t } = useI18n()
  const account = useAccount()
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const reauth = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      reauthenticateGoogleSettings(operation, password, account.mfaEnabled ? code.replace(/\s/g, '') : undefined),
    onSuccess: () => {
      setPassword('')
      setCode('')
      onConfirmed()
    },
  })
  return (
    <form
      method="post"
      className="flex flex-col gap-4"
      aria-busy={reauth.isPending}
      onSubmit={(event) => {
        event.preventDefault()
        if (!reauth.isPending) reauth.mutate()
      }}
    >
      {reauth.isError ? (
        <RequestFailureAlert
          failure={classifyRequestFailure(reauth.error)}
          problemMessages={{ INVALID_CREDENTIALS: 'login.invalidCredentials' }}
        />
      ) : null}
      <PasswordField
        label={t('google.localPasswordLabel')}
        required
        autoComplete="current-password"
        value={password}
        onChange={(event) => {
          setPassword(event.target.value)
        }}
      />
      {account.mfaEnabled ? (
        <TextField
          label={t('mfa.code')}
          required
          inputMode="numeric"
          autoComplete="one-time-code"
          pattern="[0-9 ]{6,}"
          value={code}
          onChange={(event) => {
            setCode(event.target.value)
          }}
        />
      ) : null}
      <SubmitButton
        pending={reauth.isPending}
        label={t('google.confirmIdentity')}
        pendingLabel={t('login.submitting')}
      />
    </form>
  )
}

export function GoogleLinkContinuePage(): React.JSX.Element {
  const { t } = useI18n()
  const account = useAccount()
  const cache = useQueryClient()
  const result = useOwnedSecurityResult()
  const options = useGoogleOptions()
  const flow = useGoogleFlow(options.data?.enabled === true && options.data.linkingEnabled)
  const expired = useGoogleFlowExpired(flow.data)
  const confirmation = usePrivateMutation({
    gcTime: 0,
    mutationFn: (operation) =>
      executeSecurityAction(
        operation,
        async () => {
          await confirmGoogleLink(operation)
          return { kind: 'external-login-linked' }
        },
        cache,
      ),
  })
  const failure = confirmation.error ? classifyRequestFailure(confirmation.error) : null
  const needsReauth = failure?.kind === 'problem' && failure.code === 'REAUTHENTICATION_REQUIRED'
  const allowed =
    options.data?.enabled &&
    options.data.linkingEnabled &&
    account.roles.length === 1 &&
    account.roles[0] === 'participant'
  return (
    <Page title={t('google.title')}>
      {result?.kind === 'external-login-linked' ? (
        <Alert variant="success" focusOnMount>
          {t('google.linkSuccess')}
        </Alert>
      ) : options.isPending || (allowed && flow.isPending) ? (
        <p role="status">{t('account.loading')}</p>
      ) : !allowed || flow.isError || flow.data?.nextStep !== 'Link' || expired ? (
        <Alert variant="error">{t('google.failed')}</Alert>
      ) : (
        <>
          <p>{t('google.linkWarning')}</p>
          {failure ? (
            <RequestFailureAlert
              failure={failure}
              problemMessages={{ REAUTHENTICATION_REQUIRED: 'google.reauthRequired' }}
            />
          ) : null}
          <Button
            aria-disabled={confirmation.isPending || needsReauth}
            onClick={() => {
              if (!confirmation.isPending && !needsReauth) confirmation.mutate()
            }}
          >
            {t('google.confirmLink')}
          </Button>
          {needsReauth ? (
            <GoogleLocalReauthentication
              onConfirmed={() => {
                confirmation.reset()
              }}
            />
          ) : null}
        </>
      )}
      <TextLink to={paths.account}>{t('google.backAccount')}</TextLink>
    </Page>
  )
}
