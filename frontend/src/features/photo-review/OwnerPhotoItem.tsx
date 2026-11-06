import type { Ref } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import {
  canDeletePhoto,
  citesPhotoRules,
  isInReview,
  photoStateMessage,
  rejectionMessage,
} from './owner-photo-presentation'
import type { OwnerPhoto } from './photo-review-api'
import { PhotoReviewHistoryLink } from './PhotoReviewHistoryLink'

/** Quem pode excluir e como: a pergunta em si fica no painel da página, fora da lista. */
export interface OwnerPhotoDeletionEntry {
  readonly disabled: boolean
  readonly ask: (photoId: string) => void
  readonly triggerRef: (photoId: string) => Ref<HTMLButtonElement>
}

export function OwnerPhotoItem({
  photo,
  showHistory,
  deletion,
  onShowRules,
}: {
  readonly photo: OwnerPhoto
  readonly showHistory: boolean
  /** null: esta conta não exclui fotos. */
  readonly deletion: OwnerPhotoDeletionEntry | null
  readonly onShowRules: () => void
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  return (
    <li className="flex flex-col items-start gap-2 rounded-md border border-border p-4">
      <h3 className="font-semibold break-all">{t('photoReview.photoIdentifier', { id: photo.photoId })}</h3>
      <p>{t(photoStateMessage(photo.status))}</p>
      {isInReview(photo.status) ? <p className="text-text-muted">{t('photoOwner.inReview')}</p> : null}
      {photo.status === 'Rejected' ? <p>{t(rejectionMessage(photo.rejectionCode))}</p> : null}
      {photo.status === 'Rejected' && citesPhotoRules(photo.rejectionCode) ? (
        <Button variant="neutral" onClick={onShowRules}>
          {t('photoOwner.showRules')}
        </Button>
      ) : null}
      <time dateTime={photo.createdAt}>{formatDateTime(new Date(photo.createdAt))}</time>
      {showHistory ? (
        <PhotoReviewHistoryLink
          photoId={photo.photoId}
          accessibleName={t('photoReview.historyFor', { id: photo.photoId })}
        />
      ) : null}
      {deletion !== null && canDeletePhoto(photo.status) ? (
        <Button
          ref={deletion.triggerRef(photo.photoId)}
          variant="secondary"
          aria-label={t('photoOwner.deleteFor', { id: photo.photoId })}
          aria-disabled={deletion.disabled}
          onClick={() => {
            deletion.ask(photo.photoId)
          }}
        >
          {t('photoOwner.delete')}
        </Button>
      ) : null}
    </li>
  )
}
