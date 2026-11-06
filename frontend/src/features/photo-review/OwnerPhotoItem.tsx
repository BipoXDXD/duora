import { useI18n } from '@/shared/i18n/i18n-context'
import { canDeletePhoto, isInReview, photoStateMessage, rejectionMessage } from './owner-photo-presentation'
import { OwnerPhotoDeletion } from './OwnerPhotoDeletion'
import type { OwnerPhoto } from './photo-review-api'
import { PhotoReviewHistoryLink } from './PhotoReviewHistoryLink'

export function OwnerPhotoItem({
  photo,
  userId,
  showHistory,
  onDeleted,
  onGone,
  onFailure,
}: {
  readonly photo: OwnerPhoto
  readonly userId: string
  readonly showHistory: boolean
  readonly onDeleted: () => void
  readonly onGone: () => void
  readonly onFailure: (error: unknown) => void
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  return (
    <li className="flex flex-col items-start gap-2 rounded-md border border-border p-4">
      <h2 className="font-semibold break-all">{t('photoReview.photoIdentifier', { id: photo.photoId })}</h2>
      <p>{t(photoStateMessage(photo.status))}</p>
      {isInReview(photo.status) ? <p className="text-text-muted">{t('photoOwner.inReview')}</p> : null}
      {photo.status === 'Rejected' ? <p>{t(rejectionMessage(photo.rejectionCode))}</p> : null}
      <time dateTime={photo.createdAt}>{formatDateTime(new Date(photo.createdAt))}</time>
      {showHistory ? (
        <PhotoReviewHistoryLink
          photoId={photo.photoId}
          accessibleName={t('photoReview.historyFor', { id: photo.photoId })}
        />
      ) : null}
      {canDeletePhoto(photo.status) ? (
        <OwnerPhotoDeletion
          userId={userId}
          photoId={photo.photoId}
          onDeleted={onDeleted}
          onGone={onGone}
          onFailure={onFailure}
        />
      ) : null}
    </li>
  )
}
