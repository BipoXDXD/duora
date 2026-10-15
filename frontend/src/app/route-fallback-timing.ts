/** Abaixo disto o chunk costuma chegar antes de o aviso ser lido; mostrar o texto só faria a tela piscar. */
export const ROUTE_FALLBACK_DELAY_MS = 400

/** Depois de aparecer, o aviso fica pelo menos isto, para não sumir logo depois de surgir. */
export const ROUTE_FALLBACK_MIN_VISIBLE_MS = 500

/** Quanto falta para o aviso de carregamento cumprir o tempo mínimo, dado quanto a carga levou. */
export function fallbackHoldMs(elapsedMs: number): number {
  if (elapsedMs < ROUTE_FALLBACK_DELAY_MS) {
    return 0
  }
  return Math.max(0, ROUTE_FALLBACK_DELAY_MS + ROUTE_FALLBACK_MIN_VISIBLE_MS - elapsedMs)
}

/** Entrega o resultado da carga só quando o aviso, se chegou a aparecer, já cumpriu o tempo mínimo. */
export async function withFallbackMinimum<T>(load: () => Promise<T>): Promise<T> {
  const startedAt = performance.now()
  const result = await load()
  const hold = fallbackHoldMs(performance.now() - startedAt)
  if (hold > 0) {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, hold)
    })
  }
  return result
}
