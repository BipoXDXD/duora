/** Abaixo disto o chunk costuma chegar antes de o aviso ser lido; mostrar o texto só faria a tela piscar. */
export const ROUTE_FALLBACK_DELAY_MS = 400

/** Depois de aparecer, o aviso fica pelo menos isto, para não sumir logo depois de surgir. */
export const ROUTE_FALLBACK_MIN_VISIBLE_MS = 500

// Instante (`performance.now()`) em que o texto do aviso ficou visível; `undefined` quando ele não está na
// tela. Estado de módulo de propósito: as cargas de tela nascem fora da árvore React, e a página tem um
// único fallback de rota, que marca ao mostrar o texto e limpa ao desmontar.
let fallbackVisibleSince: number | undefined

export function markRouteFallbackVisible(at: number): void {
  fallbackVisibleSince = at
}

export function clearRouteFallbackVisible(): void {
  fallbackVisibleSince = undefined
}

/** Quanto falta para o aviso, visível desde `visibleSince`, cumprir o tempo mínimo; 0 sem aviso na tela. */
export function fallbackHoldMs(visibleSince: number | undefined, now: number): number {
  if (visibleSince === undefined) {
    return 0
  }
  return Math.max(0, visibleSince + ROUTE_FALLBACK_MIN_VISIBLE_MS - now)
}

/**
 * Entrega o resultado da carga; se o aviso de carregamento estiver na tela, só depois de ele cumprir o tempo
 * mínimo. Na navegação dentro da SPA o aviso não aparece (transition), então não há espera nenhuma.
 */
export async function withFallbackMinimum<T>(load: () => Promise<T>): Promise<T> {
  const result = await load()
  const hold = fallbackHoldMs(fallbackVisibleSince, performance.now())
  if (hold > 0) {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, hold)
    })
  }
  return result
}
