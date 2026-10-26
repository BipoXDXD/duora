import { ApiProblemError, ApiResponseError, classifyRequestFailure } from '@/shared/api'
import { useEffect, useRef } from 'react'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import type { ReviewState } from './photo-review-api'
import { reviewHttpStatus } from './review-command'

const STATES: Readonly<Record<ReviewState, PlainMessageKey>> = {
  AwaitingReview: 'photoReview.awaiting',
  Approved: 'photoReview.approved',
  Rejected: 'photoReview.rejected',
  Open: 'photoReview.open',
  ClosedApproved: 'photoReview.closedApproved',
  ClosedRejected: 'photoReview.closedRejected',
  ClosedUnavailable: 'photoReview.closedUnavailable',
}

export function ReviewStateLabel({ state }: { readonly state: ReviewState }): React.JSX.Element {
  const { t } = useI18n()
  return <p>{t(STATES[state])}</p>
}

export function ReviewFailure({
  error,
  focusOnMount = false,
}: {
  readonly error: unknown
  readonly focusOnMount?: boolean
}): React.JSX.Element {
  const { t } = useI18n()
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (focusOnMount) ref.current?.focus()
  }, [focusOnMount])
  const status = reviewHttpStatus(error)
  const message =
    status === 401
      ? 'photoReview.reauthenticate'
      : status === 403
        ? 'photoReview.denied'
        : status === 404
          ? 'photoReview.notFound'
          : status === 409 || status === 412
            ? 'photoReview.conflict'
            : status === 503
              ? 'photoReview.unavailable'
              : null
  const wait = error instanceof ApiProblemError || error instanceof ApiResponseError ? error.retryAfterSeconds : null
  return (
    <div ref={ref} tabIndex={focusOnMount ? -1 : undefined}>
      {message === null ? (
        <RequestFailureAlert failure={classifyRequestFailure(error)} />
      ) : (
        <Alert variant="error">
          <p>{t(message)}</p>
          {wait === null ? null : <p>{t('photoReview.retryAfter', { seconds: wait })}</p>}
        </Alert>
      )}
    </div>
  )
}
