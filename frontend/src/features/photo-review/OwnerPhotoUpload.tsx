import { useRef, useState, type SyntheticEvent } from 'react'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import { TextField } from '@/shared/ui/form-fields'
import { SubmitButton } from '@/shared/ui/page'
import { isOwnerPhotoOutcomeUncertain } from './owner-photo-failure'
import { OwnerPhotoFailure } from './OwnerPhotoFailure'
import { checkPhotoFile, PHOTO_FILE_ACCEPT, readPhotoFile, type PhotoFileRefusal } from './owner-photo-file'
import { uploadOwnerPhoto, type PhotoUploadIntent } from './photo-review-api'
import { useReviewCommand } from './review-command'

type LocalRefusal = PhotoFileRefusal | 'missing'

const REFUSALS: Readonly<Record<LocalRefusal, PlainMessageKey>> = {
  missing: 'photoOwner.fileRequired',
  empty: 'photoOwner.fileEmpty',
  'too-large': 'photoOwner.fileTooLarge',
  'unsupported-type': 'photoOwner.fileType',
  unreadable: 'photoOwner.fileUnreadable',
}

/** Quem monta este formulário já provou que o gate do upload está aberto: a lista respondeu 200. */
export function OwnerPhotoUpload({
  onUploaded,
  onFailure,
}: {
  readonly onUploaded: () => void
  readonly onFailure: (error: unknown) => void
}): React.JSX.Element {
  const { t } = useI18n()
  const form = useRef<HTMLFormElement>(null)
  const reading = useRef(false)
  const [file, setFile] = useState<File | null>(null)
  const [refusal, setRefusal] = useState<LocalRefusal | null>(null)
  const command = useReviewCommand<PhotoUploadIntent>({
    send: uploadOwnerPhoto,
    isUncertain: isOwnerPhotoOutcomeUncertain,
    onConfirmed: onUploaded,
    onFailure: (error) => {
      // Recusa definitiva: a próxima tentativa é outra intenção, que começa escolhendo o arquivo.
      if (!isOwnerPhotoOutcomeUncertain(error)) {
        form.current?.reset()
        setFile(null)
      }
      onFailure(error)
    },
  })

  function choose(next: File | undefined): void {
    command.reset()
    setFile(next ?? null)
    setRefusal(next === undefined ? null : checkPhotoFile(next))
  }

  async function submit(event: SyntheticEvent): Promise<void> {
    event.preventDefault()
    if (command.busy || reading.current) return
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
        disabled={command.busy || command.uncertain}
        onChange={(event) => {
          choose(event.currentTarget.files?.[0])
        }}
      />
      {command.error === null ? null : <OwnerPhotoFailure error={command.error} />}
      {command.uncertain ? (
        <>
          <p>{t('photoOwner.uncertain')}</p>
          <Button
            aria-disabled={command.busy}
            onClick={() => {
              command.retry()
            }}
          >
            {t('photoReview.retrySame')}
          </Button>
        </>
      ) : (
        <SubmitButton pending={command.busy} label={t('photoOwner.send')} pendingLabel={t('photoReview.sending')} />
      )}
    </form>
  )
}
