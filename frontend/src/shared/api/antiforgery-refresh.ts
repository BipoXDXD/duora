import type { ApiClient, JsonRequest } from './api-client'

const ANTIFORGERY_TOKEN_PATH = '/api/v1/auth/csrf'

/**
 * Pede ao servidor um token antiforgery novo, entregue no cookie legível XSRF-TOKEN.
 * O token do ASP.NET Core é ligado à identidade: renove depois de login e logout (spec 8.2).
 */
export async function refreshAntiforgeryToken(client: ApiClient): Promise<void> {
  await client.sendCommand({ method: 'GET', path: ANTIFORGERY_TOKEN_PATH })
}

/**
 * Renova o token e só então envia a mutação. O custo é uma ida a mais ao servidor, em troca de
 * nunca mandar um token de outra identidade (outra aba pode ter entrado ou saído no meio).
 */
export async function sendWithFreshAntiforgeryToken(client: ApiClient, request: JsonRequest): Promise<void> {
  await refreshAntiforgeryToken(client)
  await client.sendCommand(request)
}

/** Igual a `sendWithFreshAntiforgeryToken`, para mutações que devolvem o recurso alterado. */
export async function requestJsonWithFreshAntiforgeryToken(client: ApiClient, request: JsonRequest): Promise<unknown> {
  await refreshAntiforgeryToken(client)
  return client.requestJson(request)
}
