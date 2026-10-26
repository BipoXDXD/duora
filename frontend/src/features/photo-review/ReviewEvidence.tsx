import { useEffect, useRef, useState } from 'react'
import { usePrivateOperation } from '@/features/session/private-operation'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { SelectField } from '@/shared/ui/form-fields'
import { fetchReviewEvidence, type ReviewKind } from './photo-review-api'
import { ReviewFailure } from './ReviewFeedback'

export function ReviewEvidence({
  kind,
  itemId,
  onFailure,
}: {
  readonly kind: ReviewKind
  readonly itemId: string
  readonly onFailure: (error: unknown) => void
}): React.JSX.Element {
  const { t } = useI18n()
  const privateOperation = usePrivateOperation()
  const [variant, setVariant] = useState<'card' | 'full' | 'thumb'>('card')
  const [visible, setVisible] = useState(false)
  const [unsupported, setUnsupported] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const request = useRef<AbortController | null>(null)
  const canvas = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const surface = canvas.current
    const unsubscribe = privateOperation.store.subscribe(() => {
      try {
        privateOperation.assertCurrent()
      } catch {
        request.current?.abort()
        if (surface !== null) clearPixels(surface)
        setVisible(false)
        setLoading(false)
      }
    })
    return () => {
      unsubscribe()
      request.current?.abort()
      request.current = null
      if (surface !== null) clearPixels(surface)
    }
  }, [privateOperation])

  function close(): void {
    request.current?.abort()
    request.current = null
    if (canvas.current !== null) clearPixels(canvas.current)
    setVisible(false)
    setLoading(false)
    setError(null)
  }

  async function open(): Promise<void> {
    if (request.current !== null) return
    setUnsupported(false)
    if (typeof globalThis.createImageBitmap !== 'function') {
      setUnsupported(true)
      return
    }
    const surface = canvas.current
    const context = surface?.getContext('2d') ?? null
    if (surface === null || context === null) {
      setUnsupported(true)
      return
    }
    const controller = new AbortController()
    const active = (): boolean => !controller.signal.aborted
    request.current = controller
    setLoading(true)
    setError(null)
    try {
      await privateOperation.run(async (operation) => {
        const image = await fetchReviewEvidence(operation, { kind, id: itemId, variant }, controller.signal)
        if (!active()) return
        const bitmap = await createImageBitmap(image)
        try {
          privateOperation.store.assertCurrent(operation)
          if (!active()) return
          surface.width = bitmap.width
          surface.height = bitmap.height
          context.drawImage(bitmap, 0, 0)
          setVisible(true)
        } finally {
          bitmap.close()
        }
      })
    } catch (failure) {
      try {
        privateOperation.assertCurrent()
      } catch {
        return
      }
      if (!controller.signal.aborted) {
        clearPixels(surface)
        setVisible(false)
        setError(failure)
        onFailure(failure)
        request.current = null
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }

  return (
    <section
      className="flex flex-col items-start gap-4"
      aria-label={t('photoReview.evidenceTitle')}
      aria-busy={loading}
    >
      <p>{t('photoReview.evidencePrivacy')}</p>
      <SelectField
        label={t('photoReview.variant')}
        value={variant}
        options={[
          { value: 'card', label: t('photoReview.card') },
          { value: 'full', label: t('photoReview.full') },
          { value: 'thumb', label: t('photoReview.thumb') },
        ]}
        onChange={(event) => {
          close()
          const next = event.target.value
          if (next === 'card' || next === 'full' || next === 'thumb') setVariant(next)
        }}
      />
      {error === null ? null : <ReviewFailure error={error} />}
      {unsupported ? <Alert variant="error">{t('photoReview.imageUnsupported')}</Alert> : null}
      <Button
        aria-disabled={loading || visible}
        onClick={() => {
          if (!loading && !visible) void open()
        }}
      >
        {loading ? t('photoReview.loading') : t('photoReview.viewImage')}
      </Button>
      <p role="status">
        {t(loading ? 'photoReview.loading' : visible ? 'photoReview.imageReady' : 'photoReview.imageClosed')}
      </p>
      <canvas
        ref={canvas}
        width={0}
        height={0}
        hidden={!visible}
        role="img"
        aria-label={t('photoReview.imageAlt')}
        className="max-h-96 max-w-full object-contain"
      >
        {t('photoReview.imageAlt')}
      </canvas>
      {visible || loading ? (
        <Button variant="neutral" onClick={close}>
          {t('photoReview.closeImage')}
        </Button>
      ) : null}
    </section>
  )
}

/** Resetting the dimensions clears and releases the drawing buffer; no image URL is created. */
function clearPixels(surface: HTMLCanvasElement): void {
  surface.width = 0
  surface.height = 0
}
