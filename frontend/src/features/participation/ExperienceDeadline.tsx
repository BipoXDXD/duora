import { useEffect, useState } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'

function warningAt(remaining: number): number {
  if (remaining <= 0) return 0
  if (remaining <= 30_000) return 2
  if (remaining <= 120_000) return 1
  return 0
}

/** The server owns expiry; this clock only presents its absolute deadline. */
export function ExperienceDeadline({ deadline }: { readonly deadline: string }): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const end = Date.parse(deadline)
  const [clock, setClock] = useState(() => {
    const now = Date.now()
    return { now, warning: warningAt(end - now) }
  })
  useEffect(() => {
    const update = () => {
      const now = Date.now()
      setClock((previous) => ({ now, warning: Math.max(previous.warning, warningAt(end - now)) }))
    }
    const interval = window.setInterval(update, 1000)
    // Catch up after a background tab resumes, without replaying older warnings.
    document.addEventListener('visibilitychange', update)
    return () => {
      window.clearInterval(interval)
      document.removeEventListener('visibilitychange', update)
    }
  }, [end])
  const remaining = Math.max(0, end - clock.now)
  return (
    <div className="flex flex-col gap-2">
      <p>{t('routes.deadline', { deadline: formatDateTime(new Date(deadline)) })}</p>
      <p aria-live="off">{t('participation.timeRemaining', { minutes: Math.ceil(remaining / 60_000) })}</p>
      <p role="status" aria-live="polite" aria-atomic="true" aria-label={t('participation.timeWarnings')}>
        {remaining > 0 && clock.warning > 0
          ? t(clock.warning === 2 ? 'participation.thirtySeconds' : 'participation.twoMinutes')
          : null}
      </p>
    </div>
  )
}
