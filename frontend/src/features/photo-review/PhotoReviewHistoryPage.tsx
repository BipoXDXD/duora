import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { SignInAgainButton } from '@/features/auth/SignInAgainButton'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page } from '@/shared/ui/page'
import { fetchReviewHistory, requestPhotoAppeal } from './photo-review-api'
import { needsReviewRevalidation, reviewHttpStatus, useReviewCommand } from './review-command'
import { ReviewFailure, ReviewStateLabel } from './ReviewFeedback'

export function PhotoReviewHistoryPage(): React.JSX.Element {
  const account = useAccount()
  const { photoId } = useParams<{ photoId: string }>()
  return <OwnerHistory key={`${account.id}:${photoId ?? ''}`} userId={account.id} photoId={photoId} />
}

function OwnerHistory({
  userId,
  photoId,
}: {
  readonly userId: string
  readonly photoId: string | undefined
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const client = useQueryClient()
  const privateOperation = usePrivateOperation()
  const [epoch, setEpoch] = useState(0)
  const [stopped, setStopped] = useState(false)
  const history = useQuery({
    queryKey: ['photo-review-history', userId, photoId, epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchReviewHistory(operation, photoId ?? '', signal)),
    enabled: photoId !== undefined,
    gcTime: 0,
    retry: false,
  })
  useEffect(() => {
    if (reviewHttpStatus(history.error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
  }, [history.error, client])
  const command = useReviewCommand({
    send: requestPhotoAppeal,
    onConfirmed: () => {
      void history.refetch()
    },
    onFailure: (error) => {
      if (reviewHttpStatus(error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
      if (needsReviewRevalidation(error)) {
        setStopped(true)
        void history.refetch()
      }
    },
  })
  const data = !history.isError && !stopped ? history.data : undefined
  const error = command.error ?? (history.isError ? history.error : null)
  return (
    <Page width="wide" title={t('photoReview.historyTitle')} intro={t('photoReview.ownerIntro')}>
      {error === null ? null : <ReviewFailure error={error} focusOnMount={command.error !== null} />}
      {photoId === undefined ? <Alert variant="error">{t('photoReview.notFound')}</Alert> : null}
      {reviewHttpStatus(error) === 401 ? <SignInAgainButton /> : null}
      {command.uncertain ? (
        <Button aria-disabled={command.busy} onClick={command.retry}>
          {command.busy ? t('photoReview.sending') : t('photoReview.retrySame')}
        </Button>
      ) : null}
      {command.confirmed ? (
        <Alert variant="success" focusOnMount>
          {t('photoReview.appealRequested')}
        </Alert>
      ) : null}
      {history.isPending && photoId !== undefined ? <p role="status">{t('photoReview.loading')}</p> : null}
      {history.isError || command.error !== null || stopped ? (
        <Button
          variant="secondary"
          onClick={() => {
            command.reset()
            setStopped(false)
            setEpoch((value) => value + 1)
          }}
        >
          {t('photoReview.readCurrent')}
        </Button>
      ) : null}
      {data === undefined ? null : (
        <>
          {data.items.length === 0 ? (
            <p>{t('photoReview.noHistory')}</p>
          ) : (
            <ol className="flex flex-col gap-4">
              {data.items.map((item) => (
                <li key={item.decisionId} className="rounded-md border border-border p-4">
                  <p className="font-semibold">
                    {t(item.isAppeal ? 'photoReview.appealDecision' : 'photoReview.initialDecision')}
                  </p>
                  <p>
                    {t(
                      item.reasonCode === 'meets_policy'
                        ? 'photoReview.meetsPolicy'
                        : item.reasonCode === 'does_not_meet_policy'
                          ? 'photoReview.doesNotMeetPolicy'
                          : 'photoReview.unknownReason',
                    )}
                  </p>
                  <time dateTime={item.decidedAt}>{formatDateTime(new Date(item.decidedAt))}</time>
                </li>
              ))}
            </ol>
          )}
          {data.appeal === null ? null : (
            <section aria-label={t('photoReview.appealStatus')} className="flex flex-col gap-4">
              <ReviewStateLabel state={data.appeal.state} />
              <dl className="grid gap-4">
                <div>
                  <dt>{t('photoReview.startedAt')}</dt>
                  <dd>{formatDateTime(new Date(data.appeal.requestedAt))}</dd>
                </div>
                <div>
                  <dt>{t('photoReview.responseTarget')}</dt>
                  <dd>{formatDateTime(new Date(data.appeal.responseTargetAt))}</dd>
                </div>
                <div>
                  <dt>{t('photoReview.retentionUntil')}</dt>
                  <dd>{formatDateTime(new Date(data.appeal.maximumRetentionUntil))}</dd>
                </div>
              </dl>
              <p>{t('photoReview.deadlineHint')}</p>
            </section>
          )}
          {!data.canRequestAppeal || command.confirmed || command.uncertain ? null : (
            <Button
              aria-disabled={command.busy}
              onClick={() => {
                if (!command.busy)
                  command.submit({ photoId: data.photoId, eTag: data.photoETag, key: crypto.randomUUID() })
              }}
            >
              {command.busy ? t('photoReview.sending') : t('photoReview.requestAppeal')}
            </Button>
          )}
        </>
      )}
    </Page>
  )
}
