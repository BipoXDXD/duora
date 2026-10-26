import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page } from '@/shared/ui/page'
import { fetchOwnerPhotoPage } from './photo-review-api'
import { PhotoReviewHistoryLink } from './PhotoReviewHistoryLink'
import { reviewHttpStatus } from './review-command'
import { ReviewFailure } from './ReviewFeedback'

const PHOTO_STATES: Readonly<Record<string, PlainMessageKey | undefined>> = {
  Pending: 'photoReview.photoPending',
  Quarantined: 'photoReview.photoQuarantined',
  Processing: 'photoReview.photoProcessing',
  AwaitingReview: 'photoReview.awaiting',
  Approved: 'photoReview.approved',
  Rejected: 'photoReview.rejected',
  Deleted: 'photoReview.photoDeleted',
}
const REJECTION_REASONS: Readonly<Record<string, PlainMessageKey | undefined>> = {
  PHOTO_REVIEW_REJECTED: 'photoReview.doesNotMeetPolicy',
  PHOTO_REVIEW_EXPIRED: 'photoReview.photoExpired',
  PHOTO_REVIEW_UNAVAILABLE: 'photoReview.photoUnavailable',
}

export function PhotoReviewOwnerIndex(): React.JSX.Element {
  const account = useAccount()
  return <OwnerPhotos key={account.id} userId={account.id} />
}

function OwnerPhotos({ userId }: { readonly userId: string }): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const client = useQueryClient()
  const privateOperation = usePrivateOperation()
  const [cursor, setCursor] = useState<string | null>(null)
  const [epoch, setEpoch] = useState(0)
  const photos = useQuery({
    queryKey: ['photo-review-owner-index', userId, cursor, epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchOwnerPhotoPage(operation, cursor, signal)),
    gcTime: 0,
    retry: false,
  })
  useEffect(() => {
    if (reviewHttpStatus(photos.error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
  }, [photos.error, client])
  const data = photos.isError ? undefined : photos.data
  return (
    <Page width="wide" title={t('photoReview.myPhotos')} intro={t('photoReview.ownerIndexIntro')}>
      {photos.isPending ? <p role="status">{t('photoReview.loading')}</p> : null}
      {photos.isError ? (
        <>
          <ReviewFailure error={photos.error} />
          {reviewHttpStatus(photos.error) === 400 ? (
            <Alert variant="info">{t('photoReview.pageUnavailable')}</Alert>
          ) : null}
          <Button
            variant="secondary"
            onClick={() => {
              setEpoch((value) => value + 1)
            }}
          >
            {t('photoReview.readCurrent')}
          </Button>
        </>
      ) : null}
      {data === undefined ? null : (
        <>
          {data.items.length === 0 ? (
            <p>{t('photoReview.noPhotos')}</p>
          ) : (
            <ul className="flex flex-col gap-6">
              {data.items.map((photo) => {
                const state = Object.hasOwn(PHOTO_STATES, photo.status) ? PHOTO_STATES[photo.status] : undefined
                const reason =
                  photo.rejectionCode !== null && Object.hasOwn(REJECTION_REASONS, photo.rejectionCode)
                    ? REJECTION_REASONS[photo.rejectionCode]
                    : undefined
                return (
                  <li
                    key={photo.photoId}
                    className="flex flex-col items-start gap-2 rounded-md border border-border p-4"
                  >
                    <h2 className="font-semibold break-all">
                      {t('photoReview.photoIdentifier', { id: photo.photoId })}
                    </h2>
                    <p>{t(state ?? 'photoReview.unknownPhotoState')}</p>
                    {reason === undefined ? null : <p>{t(reason)}</p>}
                    <time dateTime={photo.createdAt}>{formatDateTime(new Date(photo.createdAt))}</time>
                    <PhotoReviewHistoryLink
                      photoId={photo.photoId}
                      accessibleName={t('photoReview.historyFor', { id: photo.photoId })}
                    />
                  </li>
                )
              })}
            </ul>
          )}
          {data.nextCursor === null ? null : (
            <Button
              aria-disabled={photos.isFetching}
              onClick={() => {
                if (!photos.isFetching) setCursor(data.nextCursor)
              }}
            >
              {t('photoReview.nextPage')}
            </Button>
          )}
        </>
      )}
      {cursor === null ? null : (
        <Button
          variant="neutral"
          onClick={() => {
            setCursor(null)
          }}
        >
          {t('photoReview.firstPage')}
        </Button>
      )}
    </Page>
  )
}
