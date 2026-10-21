import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import { isUnauthorized } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import { fetchRegionDetectionCapability, suggestRegion } from './region-detection-api'

/** A transient observation belongs to this mounted form, never the query cache or another page. */
export function RegionSuggestion({
  onCountry,
}: {
  readonly onCountry: (country: 'BR') => boolean
}): React.JSX.Element | null {
  const { id } = useAccount()
  const capability = useQuery({
    queryKey: ['account', id, 'region-detection-capability'],
    queryFn: ({ signal }) => fetchRegionDetectionCapability(signal),
    gcTime: 0,
  })
  return capability.isError || capability.data !== true ? null : <SuggestionControls key={id} onCountry={onCountry} />
}

function SuggestionControls({ onCountry }: { readonly onCountry: (country: 'BR') => boolean }): React.JSX.Element {
  const { t } = useI18n()
  const queries = useQueryClient()
  const request = useRef<{ sequence: number; controller: AbortController | null }>({ sequence: 0, controller: null })
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState<
    'applied' | 'kept' | 'unknown' | 'unsupported' | 'unavailable' | 'disabled' | null
  >(null)
  useEffect(() => {
    const current = request.current
    return () => {
      current.sequence++
      current.controller?.abort()
    }
  }, [])
  const cancel = () => {
    request.current.sequence++
    request.current.controller?.abort()
    request.current.controller = null
    setPending(false)
    setMessage(null)
  }
  const start = async () => {
    cancel()
    const sequence = request.current.sequence
    const controller = new AbortController()
    request.current.controller = controller
    setPending(true)
    try {
      const result = await suggestRegion(controller.signal)
      if (sequence !== request.current.sequence || controller.signal.aborted) return
      setMessage(result.status === 'detected' ? (onCountry(result.countryCode) ? 'applied' : 'kept') : result.status)
    } catch (error) {
      if (isUnauthorized(error) && !controller.signal.aborted) void queries.invalidateQueries({ queryKey: meQueryKey })
      if (sequence === request.current.sequence && !controller.signal.aborted) setMessage('unavailable')
    } finally {
      if (sequence === request.current.sequence) {
        setPending(false)
        request.current.controller = null
      }
    }
  }
  return (
    <div className="flex flex-col items-start gap-2">
      <p>{t('regionSuggestion.hint')}</p>
      <Button
        type="button"
        variant="secondary"
        onClick={() => {
          void start()
        }}
      >
        {t('regionSuggestion.action')}
      </Button>
      {pending ? (
        <>
          <p role="status">{t('regionSuggestion.loading')}</p>
          <Button type="button" variant="secondary" onClick={cancel}>
            {t('regionSuggestion.cancel')}
          </Button>
        </>
      ) : null}
      {message === null ? null : <p role="status">{t(`regionSuggestion.${message}`)}</p>}
    </div>
  )
}
