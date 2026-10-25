import { useI18n } from '@/shared/i18n/i18n-context'
import { TextLink } from '@/shared/ui/page'

/** Embed next to the owner's existing photo/status; never selects or changes a profile photo. */
export function PhotoReviewHistoryLink({
  photoId,
  accessibleName,
}: {
  readonly photoId: string
  readonly accessibleName?: string
}): React.JSX.Element {
  const { t } = useI18n()
  return (
    <TextLink to={`/photos/${encodeURIComponent(photoId)}/review`} aria-label={accessibleName}>
      {t('photoReview.historyTitle')}
    </TextLink>
  )
}
