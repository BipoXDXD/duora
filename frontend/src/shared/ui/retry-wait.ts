import type { MessageValues } from '@/shared/i18n/catalog'

const SECONDS_PER_MINUTE = 60

/**
 * Espera do Retry-After para a mensagem `common.rateLimited`: segundos abaixo de um minuto,
 * minutos arredondados para cima, e `unknown` sem o header.
 */
export function retryWait(seconds: number | null): MessageValues<'common.rateLimited'> {
  if (seconds === null) {
    return { unit: 'unknown', count: 0 }
  }
  if (seconds < SECONDS_PER_MINUTE) {
    return { unit: 'seconds', count: Math.max(seconds, 1) }
  }
  return { unit: 'minutes', count: Math.ceil(seconds / SECONDS_PER_MINUTE) }
}
