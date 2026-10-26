import { usePrivateOperation } from '@/features/session/private-operation'
import type { OperationHandle } from '@/shared/session-context'
import { useEffect, useRef, useState } from 'react'
import { ApiProblemError, ApiResponseError } from '@/shared/api'

export function reviewHttpStatus(error: unknown): number | null {
  return error instanceof ApiProblemError || error instanceof ApiResponseError ? error.status : null
}

export function needsReviewRevalidation(error: unknown): boolean {
  return [401, 403, 409, 412, 404].includes(reviewHttpStatus(error) ?? 0)
}

/** A journal in memory only. Uncertain outcomes can only retry the exact same intent/key. */
interface ReviewCommand<Intent> {
  readonly intent: Intent | null
  readonly busy: boolean
  readonly error: unknown
  readonly confirmed: boolean
  readonly uncertain: boolean
  readonly submit: (next: Intent) => void
  readonly retry: () => void
  readonly reset: () => void
}

export function useReviewCommand<Intent>(options: {
  readonly send: (operation: OperationHandle, intent: Intent, signal: AbortSignal) => Promise<unknown>
  readonly onConfirmed: () => void
  readonly onFailure: (error: unknown) => void
}): ReviewCommand<Intent> {
  const privateOperation = usePrivateOperation()
  const [intent, setIntent] = useState<Intent | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [confirmed, setConfirmed] = useState(false)
  const lifetime = useRef<AbortController | null>(null)
  const inFlight = useRef(false)
  useEffect(() => {
    const controller = new AbortController()
    lifetime.current = controller
    return () => {
      controller.abort()
      lifetime.current = null
    }
  }, [])
  const uncertain = error !== null && ![400, 401, 403, 404, 409, 412, 428].includes(reviewHttpStatus(error) ?? 0)

  async function send(next: Intent): Promise<void> {
    const controller = lifetime.current
    if (inFlight.current || controller === null) return
    inFlight.current = true
    setBusy(true)
    setIntent(next)
    setError(null)
    try {
      await privateOperation.run((operation) => options.send(operation, next, controller.signal))
      privateOperation.assertCurrent()
      if (!controller.signal.aborted) {
        setConfirmed(true)
        options.onConfirmed()
      }
    } catch (failure) {
      try {
        privateOperation.assertCurrent()
      } catch {
        return
      }
      if (!controller.signal.aborted) {
        setError(failure)
        options.onFailure(failure)
      }
    } finally {
      inFlight.current = false
      if (!controller.signal.aborted) setBusy(false)
    }
  }

  return {
    intent,
    busy,
    error,
    confirmed,
    uncertain,
    submit: (next: Intent): void => {
      if (intent === null && !confirmed) void send(next)
    },
    retry: (): void => {
      if (intent !== null && error !== null) void send(intent)
    },
    reset: (): void => {
      if (!uncertain && !inFlight.current) {
        setIntent(null)
        setError(null)
        setConfirmed(false)
      }
    },
  }
}
