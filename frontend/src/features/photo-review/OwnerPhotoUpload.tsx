import { useEffect, useRef, useState, type SyntheticEvent } from 'react'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import { TextField } from '@/shared/ui/form-fields'
import { isUploadOutcomeUncertain } from './owner-photo-failure'
import { OwnerPhotoFailure } from './OwnerPhotoFailure'
import { checkPhotoFile, PHOTO_FILE_ACCEPT, readPhotoFile, type PhotoFileRefusal } from './owner-photo-file'
import { uploadOwnerPhoto, type PhotoUploadIntent } from './photo-review-api'
import { useReviewCommand } from './review-command'
import { useLeaveWarning } from './use-leave-warning'

type LocalRefusal = PhotoFileRefusal | 'missing'

const REFUSALS: Readonly<Record<LocalRefusal, PlainMessageKey>> = {
  missing: 'photoOwner.fileRequired',
  empty: 'photoOwner.fileEmpty',
  'too-large': 'photoOwner.fileTooLarge',
  'unsupported-type': 'photoOwner.fileType',
  unreadable: 'photoOwner.fileUnreadable',
}

/**
 * A intenção (chave e bytes) vive aqui e só aqui: quem monta este componente o mantém montado durante as releituras
 * da lista. Sem `available` (gate fechado ou conta que não envia) ele some, mas segura um pedido ainda pendente.
 */
export function OwnerPhotoUpload({
  available,
  onUploaded,
  onFailure,
}: {
  readonly available: boolean
  readonly onUploaded: () => void
  readonly onFailure: (error: unknown) => void
}): React.JSX.Element | null {
  const { t } = useI18n()
  const form = useRef<HTMLFormElement>(null)
  const reading = useRef(false)
  const [picked, setPicked] = useState<File | null>(null)
  const [refusal, setRefusal] = useState<LocalRefusal | null>(null)
  const command = useReviewCommand<PhotoUploadIntent>({
    send: uploadOwnerPhoto,
    isUncertain: isUploadOutcomeUncertain,
    onConfirmed: onUploaded,
    onFailure,
  })
  const pending = command.busy || command.uncertain
  // Recusa conclusiva: a próxima tentativa é outra intenção, que começa escolhendo o arquivo.
  const refused = command.error !== null && !command.uncertain
  const file = refused ? null : picked
  useLeaveWarning(pending)
  useEffect(() => {
    if (refused) form.current?.reset()
  }, [refused])

  function choose(next: File | undefined): void {
    command.reset()
    setPicked(next ?? null)
    setRefusal(next === undefined ? null : checkPhotoFile(next))
  }

  async function submit(event: SyntheticEvent): Promise<void> {
    event.preventDefault()
    if (command.busy || reading.current) return
    if (command.uncertain) {
      command.retry()
      return
    }
    if (file === null) {
      setRefusal('missing')
      return
    }
    reading.current = true
    const result = await readPhotoFile(file)
    reading.current = false
    if (result.kind === 'refused') setRefusal(result.reason)
    else command.submit({ key: crypto.randomUUID(), photo: result.photo })
  }

  if (!available && !pending) return null
  return (
    <form
      ref={form}
      noValidate
      className="flex flex-col gap-4"
      onSubmit={(event) => {
        void submit(event)
      }}
    >
      <h2 className="text-xl font-semibold">{t('photoOwner.uploadTitle')}</h2>
      <TextField
        type="file"
        accept={PHOTO_FILE_ACCEPT}
        label={t('photoOwner.fileLabel')}
        hint={t('photoOwner.fileHint')}
        error={refusal === null ? undefined : t(REFUSALS[refusal])}
        disabled={pending}
        onChange={(event) => {
          choose(event.currentTarget.files?.[0])
        }}
      />
      {command.error === null ? null : <OwnerPhotoFailure error={command.error} />}
      {command.uncertain ? <p>{t('photoOwner.uncertain')}</p> : null}
      {/* Um botão só, que muda de texto: trocar de elemento derrubaria o foco de quem repete o pedido. */}
      <Button type="submit" aria-disabled={command.busy} className="w-full sm:w-auto">
        {t(command.busy ? 'photoReview.sending' : command.uncertain ? 'photoReview.retrySame' : 'photoOwner.send')}
      </Button>
    </form>
  )
}
