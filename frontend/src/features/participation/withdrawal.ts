/** Estados de `GET /participation` em que a pessoa ainda tem inscrição ativa e pode retirá-la (API-E04). */
const WITHDRAWABLE_STATES: ReadonlySet<string> = new Set(['confirmed', 'waitlisted', 'checkedin', 'waiting'])

/**
 * A retirada só é oferecida com inscrição ativa e antes de `startsAt`, o mesmo corte do servidor (API-E04): depois
 * do início ele responderia `404`.
 */
export function canWithdrawRegistration(state: string | undefined, startsAt: string, now: number): boolean {
  return state !== undefined && WITHDRAWABLE_STATES.has(state) && now < Date.parse(startsAt)
}
