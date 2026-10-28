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
import { fetchReviewHistory, type PhotoReviewHistory } from './photo-review-api'
import { reviewHttpStatus } from './review-command'
import { ReviewFailure } from './ReviewFeedback'

export function PhotoReviewHistoryPage(): React.JSX.Element {
  const account = useAccount()
  const { photoId } = useParams<{ photoId: string }>()
  return <OwnerHistory key={`${account.id}:${photoId ?? ''}`} userId={account.id} photoId={photoId} />
}

/** There is no appeal: a rejected photo is final, and the owner may send a new photo for a new review. */
function isRejected(history: PhotoReviewHistory): boolean {
  return history.items.some((item) => item.decision === 'reject')
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
  const data = history.isError ? undefined : history.data
  return (
    <Page width="wide" title={t('photoReview.historyTitle')} intro={t('photoReview.ownerIntro')}>
      {history.isError ? <ReviewFailure error={history.error} /> : null}
      {photoId === undefined ? <Alert variant="error">{t('photoReview.notFound')}</Alert> : null}
      {reviewHttpStatus(history.error) === 401 ? <SignInAgainButton /> : null}
      {history.isPending && photoId !== undefined ? <p role="status">{t('photoReview.loading')}</p> : null}
      {history.isError ? (
        <Button
          variant="secondary"
          onClick={() => {
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
                  <p className="font-semibold">{t('photoReview.decision')}</p>
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
          {isRejected(data) ? <p>{t('photoReview.sendNewPhoto')}</p> : null}
        </>
      )}
    </Page>
  )
}
