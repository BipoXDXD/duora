import { createApiClient } from './api-client'
import { readAntiforgeryTokenFromCookies } from './antiforgery'

export type { ApiClient, ApiFetch, HttpMethod, JsonRequest } from './api-client'
export { ApiError, ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'
export type { ProblemDetails } from './problem-details'

/** Cliente HTTP da SPA no navegador; testes criam o seu com `createApiClient`. */
export const apiClient = createApiClient({
  fetch: (request) => globalThis.fetch(request),
  origin: globalThis.location.origin,
  readAntiforgeryToken: () => readAntiforgeryTokenFromCookies(document.cookie),
})
