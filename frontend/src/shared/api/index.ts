import { DEFAULT_LOCALE, matchLocale } from '@/shared/i18n/locale'
import { createApiClient, type JsonRequest } from './api-client'
import { readAntiforgeryTokenFromCookies } from './antiforgery'
import { sendWithFreshAntiforgeryToken } from './antiforgery-refresh'

export type { ApiClient, ApiFetch, HttpMethod, JsonRequest } from './api-client'
export { refreshAntiforgeryToken } from './antiforgery-refresh'
export { ApiError, ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'
export type { ProblemDetails } from './problem-details'
export { classifyRequestFailure, isUnauthorized, type RequestFailure } from './request-failure'

/** Cliente HTTP da SPA no navegador; testes criam o seu com `createApiClient`. */
export const apiClient = createApiClient({
  fetch: (request) => globalThis.fetch(request),
  origin: globalThis.location.origin,
  readAntiforgeryToken: () => readAntiforgeryTokenFromCookies(document.cookie),
  // O I18nProvider mantém o <html lang> igual ao idioma ativo; é a mesma fonte que leitores de tela usam.
  readLocale: () => matchLocale(document.documentElement.lang) ?? DEFAULT_LOCALE,
  now: () => Date.now(),
})

/** Envia uma mutação sem corpo de resposta, sempre com token antiforgery recém-emitido. */
export function sendMutation(request: JsonRequest): Promise<void> {
  return sendWithFreshAntiforgeryToken(apiClient, request)
}
