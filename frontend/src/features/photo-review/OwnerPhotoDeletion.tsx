import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import { isOwnerPhotoOutcomeUncertain } from './owner-photo-failure'
import { OwnerPhotoFailure } from './OwnerPhotoFailure'
import { photoStateMessage } from './owner-photo-presentation'
import { deleteOwnerPhoto, fetchOwnerPhotoVersion, type PhotoDeletionIntent } from './photo-review-api'
import { useReviewCommand, type ReviewCommand } from './review-command'

interface DeletionTarget {
  readonly userId: string
  readonly photoId: string
  readonly onDeleted: () => void
  /** A foto já não existe para esta conta: a lista precisa ser relida. */
  readonly onGone: () => void
  readonly onFailure: (error: unknown) => void
}

export function OwnerPhotoDeletion(target: DeletionTarget): React.JSX.Element {
  const { t } = useI18n()
  const trigger = useRef<HTMLButtonElement>(null)
  const returningFocus = useRef(false)
  const [asking, setAsking] = useState(false)
  useEffect(() => {
    if (!asking && returningFocus.current) trigger.current?.focus()
    returningFocus.current = false
  }, [asking])
  return asking ? (
    <DeletionConfirmation
      {...target}
      onKeep={() => {
        returningFocus.current = true
        setAsking(false)
      }}
    />
  ) : (
    <Button
      ref={trigger}
      variant="secondary"
      aria-label={t('photoOwner.deleteFor', { id: target.photoId })}
      onClick={() => {
        setAsking(true)
      }}
    >
      {t('photoOwner.delete')}
    </Button>
  )
}

/**
 * A lista não traz ETag: a versão vem de API-M02 antes de perguntar, para que a confirmação valha para o estado que
 * a pessoa leu. Se a foto mudar depois disso, o DELETE responde 412 e nada acontece.
 */
function DeletionConfirmation({
  userId,
  photoId,
  onDeleted,
  onGone,
  onFailure,
  onKeep,
}: DeletionTarget & { readonly onKeep: () => void }): React.JSX.Element {
  const { t } = useI18n()
  const titleId = useId()
  const panel = useRef<HTMLDivElement>(null)
  const privateOperation = usePrivateOperation()
  const [epoch, setEpoch] = useState(0)
  const version = useQuery({
    queryKey: ['owner-photo-version', userId, photoId, epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchOwnerPhotoVersion(operation, photoId, signal)),
    gcTime: 0,
    retry: false,
  })
  const command = useReviewCommand<PhotoDeletionIntent>({
    send: deleteOwnerPhoto,
    isUncertain: isOwnerPhotoOutcomeUncertain,
    onConfirmed: onDeleted,
    onFailure,
  })
  const current = version.isError ? undefined : version.data
  useEffect(() => {
    if (version.error !== null) onFailure(version.error)
  }, [version.error, onFailure])
  useEffect(() => {
    if (current?.status === 'Deleted') onGone()
    else if (current !== undefined) panel.current?.focus()
  }, [current, onGone])

  return (
    <div ref={panel} role="group" aria-labelledby={titleId} tabIndex={-1} className="flex flex-col items-start gap-2">
      <p id={titleId} className="font-semibold">
        {t('photoOwner.confirmDeletion')}
      </p>
      {version.isPending ? <p role="status">{t('photoOwner.readingState')}</p> : null}
      {version.isError ? <OwnerPhotoFailure error={version.error} /> : null}
      {current === undefined ? null : (
        <p>{t('photoOwner.currentState', { state: t(photoStateMessage(current.status)) })}</p>
      )}
      {command.error === null ? null : <OwnerPhotoFailure error={command.error} />}
      {command.uncertain ? <p>{t('photoOwner.uncertain')}</p> : null}
      <DeletionActions
        command={command}
        canConfirm={current !== undefined}
        onConfirm={() => {
          if (current !== undefined) command.submit({ photoId, eTag: current.eTag, key: crypto.randomUUID() })
        }}
        onReread={() => {
          command.reset()
          setEpoch((value) => value + 1)
        }}
        onKeep={onKeep}
      />
    </div>
  )
}

function DeletionActions({
  command,
  canConfirm,
  onConfirm,
  onReread,
  onKeep,
}: {
  readonly command: ReviewCommand<PhotoDeletionIntent>
  readonly canConfirm: boolean
  readonly onConfirm: () => void
  readonly onReread: () => void
  readonly onKeep: () => void
}): React.JSX.Element {
  const { t } = useI18n()
  const refused = command.error !== null && !command.uncertain
  return (
    <div className="flex flex-wrap gap-2">
      {canConfirm && (command.intent === null || command.busy) ? (
        <Button variant="destructive" aria-disabled={command.busy} onClick={onConfirm}>
          {t(command.busy ? 'photoOwner.deleting' : 'photoOwner.confirm')}
        </Button>
      ) : null}
      {command.uncertain ? (
        <Button
          onClick={() => {
            command.retry()
          }}
        >
          {t('photoReview.retrySame')}
        </Button>
      ) : null}
      {refused ? (
        <Button variant="secondary" onClick={onReread}>
          {t('photoOwner.rereadPhoto')}
        </Button>
      ) : null}
      {command.busy || command.uncertain ? null : (
        <Button variant="neutral" onClick={onKeep}>
          {t('photoOwner.keep')}
        </Button>
      )}
    </div>
  )
}
