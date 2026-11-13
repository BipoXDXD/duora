import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
import { canAccountDeletePhotos, PHOTO_RULES } from './owner-photo-presentation'
import { OwnerPhotoDeletion } from './OwnerPhotoDeletion'
import { OwnerPhotoItem, type OwnerPhotoDeletionEntry } from './OwnerPhotoItem'
import { OwnerPhotoUpload } from './OwnerPhotoUpload'
import { fetchOwnerPhotoPage } from './photo-review-api'
import { reviewHttpStatus } from './review-command'
import { ReviewFailure } from './ReviewFeedback'

/** Espelha `PhotoUploadPolicy`; estado desconhecido não oferece envio, e o servidor continua decidindo. */
const UPLOADING_STATUSES: ReadonlySet<AccountStatus> = new Set(['Active', 'PendingEligibility'])

type Notice = 'photoOwner.uploaded' | 'photoOwner.deleted' | 'photoOwner.abandoned'

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
  const listTitle = useRef<HTMLHeadingElement>(null)
  const rulesTitle = useRef<HTMLHeadingElement>(null)
  const triggers = useRef(new Map<string, HTMLButtonElement>())
  const [cursor, setCursor] = useState<string | null>(null)
  const [epoch, setEpoch] = useState(0)
  const [uploads, setUploads] = useState(0)
  const [notice, setNotice] = useState<Notice | null>(null)
  const [opened, setOpened] = useState(false)
  const [closed, setClosed] = useState(false)
  const [deleting, setDeleting] = useState<string | null>(null)
  const photos = useQuery({
    queryKey: ['photo-review-owner-index', userId, cursor, epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchOwnerPhotoPage(operation, cursor, signal)),
    gcTime: 0,
    retry: false,
  })
  useEffect(() => {
    if (reviewHttpStatus(photos.error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
  }, [photos.error, client])
  // Só resposta do servidor muda o gate: uma releitura em curso, ou que falhe por outro motivo, não desmonta nada.
  if (photos.isSuccess && (!opened || closed)) {
    setOpened(true)
    setClosed(false)
  } else if (photos.isError && isUploadsDisabled(photos.error) && !closed) setClosed(true)
  const data = photos.isError ? undefined : photos.data
  const uploader = UPLOADING_STATUSES.has(status)

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
  // A chave do formulário descarta a intenção: depois de confirmada, ou quando a pessoa desiste dela.
  const restartUpload = useCallback(
    (next: Notice) => {
      setNotice(next)
      setUploads((value) => value + 1)
      setCursor(null)
      reread()
    },
    [reread],
  )
  const onUploaded = useCallback(() => {
    restartUpload('photoOwner.uploaded')
  }, [restartUpload])
  const onAbandoned = useCallback(() => {
    restartUpload('photoOwner.abandoned')
  }, [restartUpload])
  const showRules = useCallback(() => {
    rulesTitle.current?.focus()
  }, [])
  const onDeleted = useCallback(() => {
    setNotice('photoOwner.deleted')
    setDeleting(null)
    reread()
  }, [reread])
  const onDeletionGone = useCallback(() => {
    setDeleting(null)
    listTitle.current?.focus()
    reread()
  }, [reread])
  const onKeep = useCallback(() => {
    const trigger = deleting === null ? undefined : triggers.current.get(deleting)
    ;(trigger ?? listTitle.current)?.focus()
    setDeleting(null)
  }, [deleting])
  const deletion = useMemo<OwnerPhotoDeletionEntry>(
    () => ({
      disabled: deleting !== null,
      ask: (photoId) => {
        setDeleting((open) => open ?? photoId)
      },
      triggerRef: (photoId) => (button) => {
        if (button !== null) triggers.current.set(photoId, button)
        return () => {
          triggers.current.delete(photoId)
        }
      },
    }),
    [deleting],
  )
  function goTo(next: string | null): void {
    setCursor(next)
    listTitle.current?.focus()
  }

  return (
    <Page
      width="wide"
      title={t('photoReview.myPhotos')}
      {...(closed ? {} : { intro: t('photoReview.ownerIndexIntro') })}
    >
      {notice === null ? null : (
        <Alert key={epoch} variant={notice === 'photoOwner.abandoned' ? 'info' : 'success'} focusOnMount>
          {t(notice)}
        </Alert>
      )}
      {closed ? (
        <Alert variant="info" focusOnMount={opened}>
          {t('photoOwner.disabled')}
        </Alert>
      ) : null}
      {photos.isPending && !opened ? <p role="status">{t('photoOwner.loading')}</p> : null}
      {opened ? (
        <OwnerPhotoUpload
          key={uploads}
          available={!closed && uploader}
          onUploaded={onUploaded}
          onAbandoned={onAbandoned}
          onFailure={reactToFailure}
        />
      ) : null}
      {opened && !closed && !uploader ? <p>{t('photoOwner.accountCannotUpload')}</p> : null}
      {opened && !closed && uploader ? (
        <section className="flex flex-col gap-2">
          <h2 ref={rulesTitle} tabIndex={-1} className="text-xl font-semibold">
            {t('photoOwner.rulesTitle')}
          </h2>
          <p className="text-text-muted">{t('photoOwner.rulesIntro')}</p>
          <ul className="list-disc ps-6">
            {PHOTO_RULES.map((rule) => (
              <li key={rule}>{t(rule)}</li>
            ))}
          </ul>
        </section>
      ) : null}
      {deleting === null ? null : (
        <OwnerPhotoDeletion
          key={deleting}
          userId={userId}
          photoId={deleting}
          closed={closed}
          onDeleted={onDeleted}
          onGone={onDeletionGone}
          onFailure={reactToFailure}
          onKeep={onKeep}
        />
      )}
      {opened && !closed ? (
        <h2 ref={listTitle} tabIndex={-1} className="text-xl font-semibold">
          {t('photoOwner.listTitle')}
        </h2>
      ) : null}
      {photos.isPending && opened ? <p role="status">{t('photoOwner.loading')}</p> : null}
      {photos.isError && !closed ? (
        <>
          <ReviewFailure error={photos.error} />
          {reviewHttpStatus(photos.error) === 400 ? (
            <Alert variant="info">{t('photoReview.pageUnavailable')}</Alert>
          ) : null}
        </>
      ) : null}
      {data === undefined ? null : data.items.length === 0 ? (
        <p>{t('photoReview.noPhotos')}</p>
      ) : (
        <ul className="flex flex-col gap-6">
          {data.items.map((photo) => (
            <OwnerPhotoItem
              key={photo.photoId}
              photo={photo}
              // A conta suspensa não alcança o histórico de revisão (ADR-0042).
              showHistory={status !== 'Suspended'}
              deletion={canAccountDeletePhotos(status) ? deletion : null}
              onShowRules={uploader ? showRules : null}
            />
          ))}
        </ul>
      )}
      {!closed && (opened || photos.isError) ? (
        <Button
          variant="secondary"
          onClick={() => {
            setNotice(null)
            reread()
          }}
        >
          {t('photoReview.readCurrent')}
        </Button>
      ) : null}
      {data?.nextCursor === null || data === undefined ? null : (
        <Button
          aria-disabled={photos.isFetching}
          onClick={() => {
            if (!photos.isFetching) goTo(data.nextCursor)
          }}
        >
          {t('photoReview.nextPage')}
        </Button>
      )}
      {cursor === null || closed ? null : (
        <Button
          variant="neutral"
          onClick={() => {
            goTo(null)
          }}
        >
          {t('photoReview.firstPage')}
        </Button>
      )}
    </Page>
  )
}
