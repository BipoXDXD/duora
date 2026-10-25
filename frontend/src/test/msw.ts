import { syntheticLegalCatalog } from './legal-documents'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

/** Servidor MSW dos testes de componente; cada teste declara os handlers que usa com `server.use`. */
export const server = setupServer(
  http.get('/api/v1/legal-documents', () => HttpResponse.json(syntheticLegalCatalog)),
  http.get('/api/v1/me/region-detection/capability', () => HttpResponse.json({ enabled: false })),
)

export const API = '/api/v1'
const ANTIFORGERY_COOKIE = 'XSRF-TOKEN'

/**
 * Simula GET /auth/csrf: cada chamada emite um token novo no cookie legível, como o backend.
 * Devolve o token mais recente para o teste comparar com o header da mutação.
 */
export function useAntiforgeryEndpoint(): { readonly latestToken: () => string | null; readonly calls: () => number } {
  let issued = 0
  server.use(
    http.get(`${API}/auth/csrf`, () => {
      issued += 1
      document.cookie = `${ANTIFORGERY_COOKIE}=token-${String(issued)}; path=/`
      return new HttpResponse(null, { status: 204 })
    }),
  )
  return {
    latestToken: () => (issued === 0 ? null : `token-${String(issued)}`),
    calls: () => issued,
  }
}

export function clearAntiforgeryCookie(): void {
  document.cookie = `${ANTIFORGERY_COOKIE}=; Max-Age=0; path=/`
}

export function problem(status: number, code: string, headers: Record<string, string> = {}): HttpResponse<string> {
  return new HttpResponse(JSON.stringify({ type: 'about:blank', title: code, status, code }), {
    status,
    headers: { 'Content-Type': 'application/problem+json', ...headers },
  })
}
