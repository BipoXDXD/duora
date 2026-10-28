import { paths } from '@/shared/paths'

/**
 * A borda (infra/caddy/Caddyfile) só concede câmera, WASM e os hosts da AWS ao documento carregado exatamente em
 * /account/selfie. Uma navegação interna da SPA não troca a CSP do documento, então a página da selfie exige um
 * documento próprio e o descarta ao sair.
 */
export function isSelfieDocument(): boolean {
  const [entry] = performance.getEntriesByType('navigation')
  if (entry === undefined) return false
  try {
    return new URL(entry.name).pathname === paths.selfie
  } catch {
    return false
  }
}

export function loadSelfieDocument(): void {
  window.location.replace(paths.selfie)
}

/** Recarrega a rota atual para ela voltar à política comum da borda. */
export function reloadCurrentDocument(): void {
  window.location.reload()
}
