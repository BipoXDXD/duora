import { useEffect, useRef, useState } from 'react'
import { usePrivateOperation } from '@/features/session/private-operation'
import { useI18n } from '@/shared/i18n/i18n-context'
import { pagePrivateAudio } from '@/shared/page-private-audio'
import {
  loadOwnedAudio,
  type AudioLease,
  type AudioSnapshot,
  type PrivateAudioCoordinator,
} from '@/shared/private-audio'
import { Button } from '@/shared/ui/button'

const EMPTY: AudioSnapshot = { phase: 'empty', duration: 0, muted: true }

/** Sem src nativo/preload: o primeiro gesto captura a operação e obtém os bytes pela fronteira privada. */
export function PrivateAudioPlayer({
  path,
  coordinator = pagePrivateAudio,
}: {
  readonly path: string
  readonly coordinator?: PrivateAudioCoordinator
}): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { t } = useI18n()
  const current = useRef<{ readonly lease: AudioLease; readonly unsubscribe: () => void } | null>(null)
  const mounted = useRef(true)
  const [view, setView] = useState(EMPTY)
  const [position, setPosition] = useState(0)
  const [failure, setFailure] = useState<'busy' | 'unavailable' | null>(null)
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      current.current?.unsubscribe()
      current.current?.lease.dispose()
      current.current = null
    }
  }, [])
  useEffect(() => {
    if (view.phase !== 'playing') return
    const timer = window.setInterval(() => {
      setPosition(current.current?.lease.position() ?? 0)
    }, 200)
    return () => {
      window.clearInterval(timer)
    }
  }, [view.phase])
  const perform = async (action: () => Promise<void>): Promise<void> => {
    setFailure(null)
    try {
      await action()
    } catch (error) {
      if (!mounted.current || (error instanceof DOMException && error.name === 'AbortError')) return
      try {
        privateOperation.assertCurrent()
      } catch {
        return
      }
      setFailure(error instanceof Error && error.message === 'audio-busy' ? 'busy' : 'unavailable')
    }
  }
  const toggle = async (): Promise<void> => {
    let value = current.current
    if (value === null || value.lease.getSnapshot().phase === 'disposed') {
      value?.unsubscribe()
      const operation = privateOperation.begin()
      let lease: AudioLease
      try {
        lease = coordinator.acquire(operation)
      } catch (error) {
        privateOperation.finish(operation)
        throw error
      }
      const update = (): void => {
        if (!mounted.current) return
        setView(lease.getSnapshot())
        setPosition(lease.position())
      }
      value = { lease, unsubscribe: lease.subscribe(update) }
      current.current = value
      update()
      await loadOwnedAudio(operation, path, lease)
    }
    if (value.lease.getSnapshot().phase === 'playing') value.lease.pause()
    else await value.lease.play()
  }
  const loading = view.phase === 'loading'
  const ready = !['empty', 'loading', 'disposed'].includes(view.phase)
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          aria-disabled={loading}
          onClick={() => {
            if (!loading) void perform(toggle)
          }}
        >
          {t(view.phase === 'playing' ? 'audio.pause' : 'audio.play')}
        </Button>
        <Button
          type="button"
          variant="secondary"
          disabled={!ready}
          onClick={() => {
            void perform(async () => {
              const lease = current.current?.lease
              if (lease === undefined) return
              lease.pause()
              await lease.seek(0)
              await lease.play()
            })
          }}
        >
          {t('audio.restart')}
        </Button>
        <Button
          type="button"
          variant="secondary"
          disabled={!ready}
          aria-pressed={!view.muted}
          onClick={() => {
            const lease = current.current?.lease
            if (lease === undefined) return
            lease.setMuted(!view.muted)
          }}
        >
          {t(view.muted ? 'audio.sound' : 'audio.mute')}
        </Button>
      </div>
      <label className="flex flex-col gap-2">
        <span>{t('audio.position')}</span>
        <input
          type="range"
          min={0}
          max={view.duration || 1}
          step={0.1}
          value={position}
          disabled={!ready}
          className="min-h-11 w-full accent-action"
          onChange={(event) => {
            const next = event.target.valueAsNumber
            setPosition(next)
            void perform(async () => {
              await current.current?.lease.seek(next)
            })
          }}
        />
      </label>
      <span aria-hidden="true" className="tabular-nums">
        {t('audio.elapsed', { position: position.toFixed(1), duration: view.duration.toFixed(1) })}
      </span>
      {loading ? <p role="status">{t('audio.loading')}</p> : null}
      {failure === null ? null : <p role="status">{t(failure === 'busy' ? 'audio.busy' : 'audio.unavailable')}</p>}
    </div>
  )
}
