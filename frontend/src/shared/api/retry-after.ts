const DELTA_SECONDS = /^\d+$/
const HTTP_DATE = /^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$/
const MILLISECONDS_PER_SECOND = 1000

/**
 * Lê o header Retry-After (RFC 9110, 10.2.3): delta-seconds ou HTTP-date.
 * Devolve os segundos de espera a partir de `nowMs`, ou null quando o valor é inválido.
 */
export function parseRetryAfterSeconds(header: string | null, nowMs: number): number | null {
  const value = header?.trim() ?? ''
  if (value === '') {
    return null
  }
  if (DELTA_SECONDS.test(value)) {
    return Number(value)
  }
  // Date.parse aceita quase qualquer coisa ("-5" vira um ano); só o formato IMF-fixdate conta.
  const dateMs = HTTP_DATE.test(value) ? Date.parse(value) : Number.NaN
  if (Number.isNaN(dateMs)) {
    return null
  }
  return Math.max(0, Math.ceil((dateMs - nowMs) / MILLISECONDS_PER_SECOND))
}
