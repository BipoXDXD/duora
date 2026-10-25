import type { ApiClient, JsonRequest } from './api-client'
import { assertPublicMutation } from './request-purpose'
import { isLogoutRequest } from './request-purpose'
import { contextAbort, type OperationHandle } from '../session-context'

const ANTIFORGERY_TOKEN_PATH = '/api/v1/auth/csrf'

/**
 * Pede ao servidor um token antiforgery novo, entregue no cookie legível XSRF-TOKEN.
 * O token do ASP.NET Core é ligado à identidade: renove depois de login e logout (spec 8.2).
 */
export async function refreshAntiforgeryToken(client: ApiClient, operation?: OperationHandle): Promise<void> {
  await client.sendCommand({
    method: 'GET',
    path: ANTIFORGERY_TOKEN_PATH,
    ...(operation === undefined ? {} : { operation }),
  })
}

/**
 * Renova a prova apenas para autenticação pública. Intenções privadas usam o contexto da sua
 * árvore e nunca podem adquirir aqui uma prova sob o cookie de outra identidade.
 */
export async function sendWithFreshAntiforgeryToken(client: ApiClient, request: JsonRequest): Promise<void> {
  assertPublicMutation(request)
  const operation = beginPublicIntent(client, request)
  try {
    await refreshAntiforgeryToken(client, operation)
    client.sessionContext.assertCurrent(operation)
    await client.sendCommand({ ...request, operation })
    client.sessionContext.assertCurrent(operation)
  } finally {
    if (request.operation === undefined) client.sessionContext.finish(operation)
  }
}

/** Igual a `sendWithFreshAntiforgeryToken`, para mutações que devolvem o recurso alterado. */
export async function requestJsonWithFreshAntiforgeryToken(client: ApiClient, request: JsonRequest): Promise<unknown> {
  assertPublicMutation(request)
  const operation = beginPublicIntent(client, request)
  try {
    await refreshAntiforgeryToken(client, operation)
    client.sessionContext.assertCurrent(operation)
    const body = await client.requestJson({ ...request, operation })
    client.sessionContext.assertCurrent(operation)
    return body
  } finally {
    if (request.operation === undefined) client.sessionContext.finish(operation)
  }
}

function beginPublicIntent(client: ApiClient, request: JsonRequest): OperationHandle {
  const state = client.sessionContext.getSnapshot()
  if (
    isLogoutRequest(request.method, request.path) &&
    (state.protected || state.owner !== null || state.pendingOwner !== null)
  ) {
    throw contextAbort()
  }
  return request.operation ?? client.sessionContext.beginPublic()
}
