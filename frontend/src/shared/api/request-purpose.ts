const PUBLIC_AUTHENTICATION_MUTATIONS = new Set([
  '/api/v1/auth/register',
  '/api/v1/auth/confirm-email',
  '/api/v1/auth/email-confirmation-requests',
  '/api/v1/auth/login',
  '/api/v1/auth/password-reset-requests',
  '/api/v1/auth/password-resets',
])

export function isPublicAuthenticationMutation(method: string, path: string): boolean {
  return method === 'POST' && PUBLIC_AUTHENTICATION_MUTATIONS.has(path)
}

export function isLogoutRequest(method: string, path: string): boolean {
  return method === 'POST' && path === '/api/v1/auth/logout'
}

export function assertPublicMutation(request: { readonly method: string; readonly path: string }): void {
  if (!isPublicAuthenticationMutation(request.method, request.path) && !isLogoutRequest(request.method, request.path)) {
    throw new DOMException('A operação privada exige um contexto confirmado.', 'AbortError')
  }
}
