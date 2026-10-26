import { usePrivateOperation } from '@/features/session/private-operation'
import { useEffect, useRef, useState } from 'react'
import { z } from 'zod'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { PasswordField, SelectField, TextField } from '@/shared/ui/form-fields'
import { SubmitButton } from '@/shared/ui/page'
import { reauthenticateReviewer, type ReviewDecision } from './photo-review-api'
import { ReviewFailure } from './ReviewFeedback'

export function ReviewDecisionForm({
  busy,
  onDecision,
}: {
  readonly busy: boolean
  readonly onDecision: (decision: ReviewDecision) => void
}): React.JSX.Element {
  const { t } = useI18n()
  const [decision, setDecision] = useState('')
  const [invalid, setInvalid] = useState(false)
  return (
    <form
      className="flex flex-col gap-4"
      aria-busy={busy}
      onSubmit={(event) => {
        event.preventDefault()
        if (busy) return
        if (decision !== 'approve' && decision !== 'reject') {
          setInvalid(true)
          return
        }
        onDecision(decision)
      }}
    >
      <SelectField
        label={t('photoReview.decision')}
        value={decision}
        error={invalid ? t('photoReview.chooseDecision') : undefined}
        options={[
          { value: '', label: t('photoReview.chooseDecision') },
          { value: 'approve', label: t('photoReview.approve') },
          { value: 'reject', label: t('photoReview.reject') },
        ]}
        onChange={(event) => {
          setDecision(event.target.value)
          setInvalid(false)
        }}
      />
      <p>{t('photoReview.closedReasons')}</p>
      <SubmitButton pending={busy} label={t('photoReview.confirmDecision')} pendingLabel={t('photoReview.sending')} />
    </form>
  )
}

export function ReviewAssignmentForm({
  busy,
  onAssign,
}: {
  readonly busy: boolean
  readonly onAssign: (reviewerId: string) => void
}): React.JSX.Element {
  const { t } = useI18n()
  const [reviewerId, setReviewerId] = useState('')
  const [invalid, setInvalid] = useState(false)
  return (
    <form
      className="flex flex-col gap-4"
      aria-busy={busy}
      onSubmit={(event) => {
        event.preventDefault()
        if (busy) return
        const valid = z.uuid().safeParse(reviewerId)
        setInvalid(!valid.success)
        if (valid.success) onAssign(valid.data)
      }}
    >
      <TextField
        label={t('photoReview.reviewerId')}
        hint={t('photoReview.assignmentHint')}
        value={reviewerId}
        autoComplete="off"
        error={invalid ? t('photoReview.invalidReviewer') : undefined}
        onChange={(event) => {
          setReviewerId(event.target.value)
        }}
      />
      <SubmitButton pending={busy} label={t('photoReview.assign')} pendingLabel={t('photoReview.sending')} />
    </form>
  )
}

export function ReviewerReauthentication({ onConfirmed }: { readonly onConfirmed: () => void }): React.JSX.Element {
  const { t } = useI18n()
  const privateOperation = usePrivateOperation()
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [confirmed, setConfirmed] = useState(false)
  const lifetime = useRef<AbortController | null>(null)
  const inFlight = useRef(false)
  useEffect(() => {
    const controller = new AbortController()
    lifetime.current = controller
    return () => {
      controller.abort()
      lifetime.current = null
    }
  }, [])

  async function submit(): Promise<void> {
    const controller = lifetime.current
    if (inFlight.current || controller === null) return
    inFlight.current = true
    setBusy(true)
    setError(null)
    try {
      await privateOperation.run((operation) =>
        reauthenticateReviewer(operation, { password, twoFactorCode: code }, controller.signal),
      )
      privateOperation.assertCurrent()
      if (!controller.signal.aborted) {
        setConfirmed(true)
        onConfirmed()
      }
    } catch (failure) {
      try {
        privateOperation.assertCurrent()
      } catch {
        return
      }
      if (!controller.signal.aborted) setError(failure)
    } finally {
      inFlight.current = false
      if (!controller.signal.aborted) {
        setBusy(false)
        setPassword('')
        setCode('')
      }
    }
  }

  return (
    <section className="flex flex-col gap-4" aria-label={t('photoReview.reauthentication')}>
      <p>{t('photoReview.mfaHint')}</p>
      {error === null ? null : <ReviewFailure error={error} />}
      {confirmed ? <Alert variant="success">{t('photoReview.reauthenticated')}</Alert> : null}
      <form
        className="flex flex-col gap-4"
        aria-busy={busy}
        onSubmit={(event) => {
          event.preventDefault()
          void submit()
        }}
      >
        <PasswordField
          label={t('login.password')}
          value={password}
          required
          autoComplete="current-password"
          onChange={(event) => {
            setPassword(event.target.value)
          }}
        />
        <TextField
          label={t('mfa.code')}
          value={code}
          required
          autoComplete="one-time-code"
          inputMode="numeric"
          pattern="[0-9]{6}"
          onChange={(event) => {
            setCode(event.target.value)
          }}
        />
        <Button type="submit" aria-disabled={busy}>
          {busy ? t('photoReview.sending') : t('photoReview.reauthentication')}
        </Button>
      </form>
    </section>
  )
}
