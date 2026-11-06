import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import type { AccountStatus } from '@/features/session/session-api'
import { isUnauthorized } from '@/shared/api'
import { isSessionRestriction } from '@/shared/api/request-failure'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page } from '@/shared/ui/page'
import { invalidatesOwnerPhotoList, isUploadsDisabled } from './owner-photo-failure'
import { OwnerPhotoItem } from './OwnerPhotoItem'
import { OwnerPhotoUpload } from './OwnerPhotoUpload'
import { fetchOwnerPhotoPage } from './photo-review-api'
import { reviewHttpStatus } from './review-command'
import { ReviewFailure } from './ReviewFeedback'

/** Espelha `PhotoUploadPolicy`; estado desconhecido não oferece envio, e o servidor continua decidindo. */
const UPLOADING_STATUSES: ReadonlySet<AccountStatus> = new Set(['Active', 'PendingEligibility'])

type Notice = 'photoOwner.uploaded' | 'photoOwner.deleted'

export function PhotoReviewOwnerIndex(): React.JSX.Element {
  const account = useAccount()
  return <OwnerPhotos key={account.id} userId={account.id} status={account.status} />
}

function OwnerPhotos({
  userId,
  status,
}: {
  readonly userId: string
  readonly status: AccountStatus
}): React.JSX.Element {
  const { t } = useI18n()
  const client = useQueryClient()
  const privateOperation = usePrivateOperation()
  const [cursor, setCursor] = useState<string | null>(null)
  const [epoch, setEpoch] = useState(0)
  const [uploads, setUploads] = useState(0)
  const [notice, setNotice] = useState<Notice | null>(null)
  const photos = useQuery({
    queryKey: ['photo-review-owner-index', userId, cursor, epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchOwnerPhotoPage(operation, cursor, signal)),
    gcTime: 0,
    retry: false,
  })
  useEffect(() => {
    if (reviewHttpStatus(photos.error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
  }, [photos.error, client])
  const reread = useCallback(() => {
    setEpoch((value) => value + 1)
  }, [])
  const reactToFailure = useCallback(
    (error: unknown) => {
      setNotice(null)
      // Estes comandos não passam pelo MutationCache, que faz esta conferência para as demais escritas.
      if (isUnauthorized(error) || isSessionRestriction(error)) void client.invalidateQueries({ queryKey: meQueryKey })
      if (invalidatesOwnerPhotoList(error)) reread()
    },
    [client, reread],
  )
  const onUploaded = useCallback(() => {
    setNotice('photoOwner.uploaded')
    setUploads((value) => value + 1)
    setCursor(null)
    reread()
  }, [reread])
  const onDeleted = useCallback(() => {
    setNotice('photoOwner.deleted')
    reread()
  }, [reread])
  const data = photos.isError ? undefined : photos.data
  const closed = photos.isError && isUploadsDisabled(photos.error)
  return (
    <Page
      width="wide"
      title={t('photoReview.myPhotos')}
      {...(closed ? {} : { intro: t('photoReview.ownerIndexIntro') })}
    >
      {notice === null ? null : (
        <Alert key={epoch} variant="success" focusOnMount>
          {t(notice)}
        </Alert>
      )}
      {closed ? <Alert variant="info">{t('photoOwner.disabled')}</Alert> : null}
      {photos.isPending ? <p role="status">{t('photoOwner.loading')}</p> : null}
      {photos.isError && !closed ? (
        <>
          <ReviewFailure error={photos.error} />
          {reviewHttpStatus(photos.error) === 400 ? (
            <Alert variant="info">{t('photoReview.pageUnavailable')}</Alert>
          ) : null}
          <Button variant="secondary" onClick={reread}>
            {t('photoReview.readCurrent')}
          </Button>
        </>
      ) : null}
      {data === undefined ? null : (
        <>
          {UPLOADING_STATUSES.has(status) ? (
            <OwnerPhotoUpload key={uploads} onUploaded={onUploaded} onFailure={reactToFailure} />
          ) : (
            <p>{t('photoOwner.accountCannotUpload')}</p>
          )}
          {data.items.length === 0 ? (
            <p>{t('photoReview.noPhotos')}</p>
          ) : (
            <ul className="flex flex-col gap-6">
              {data.items.map((photo) => (
                <OwnerPhotoItem
                  key={photo.photoId}
                  photo={photo}
                  userId={userId}
                  // A conta suspensa não alcança o histórico de revisão (ADR-0042).
                  showHistory={status !== 'Suspended'}
                  onDeleted={onDeleted}
                  onGone={reread}
                  onFailure={reactToFailure}
                />
              ))}
            </ul>
          )}
          <Button
            variant="secondary"
            onClick={() => {
              setNotice(null)
              reread()
            }}
          >
            {t('photoReview.readCurrent')}
          </Button>
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
