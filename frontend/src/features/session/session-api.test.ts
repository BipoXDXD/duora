import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { ApiProblemError } from '@/shared/api'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { fetchMe, fetchSessions, logout, revokeSession } from './session-api'

const USER_ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const SESSION_ID = '0199f1a2-3b4c-7d5e-8f60-000000000001'

describe('fetchMe', () => {
  it('devolve a conta própria só com os campos do contrato', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json({
          id: USER_ID,
          email: 'ana@exemplo.com',
          status: 'Active',
          emailConfirmed: true,
          passwordHash: 'nunca-deveria-vir',
        }),
      ),
    )

    await expect(fetchMe()).resolves.toEqual({
      id: USER_ID,
      email: 'ana@exemplo.com',
      status: 'Active',
      emailConfirmed: true,
    })
  })

  it('trata status desconhecido sem quebrar (spec 8.1)', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json({ id: USER_ID, email: 'ana@exemplo.com', status: 'NovoEstado', emailConfirmed: true }),
      ),
    )

    await expect(fetchMe()).resolves.toMatchObject({ status: 'Unknown' })
  })

  it.each([
    ['id que não é UUID', { id: '1', email: 'a@b.test', status: 'Active', emailConfirmed: true }],
    ['emailConfirmed em texto', { id: USER_ID, email: 'a@b.test', status: 'Active', emailConfirmed: 'true' }],
    ['sem e-mail', { id: USER_ID, status: 'Active', emailConfirmed: true }],
    ['null', null],
  ])('rejeita payload fora do contrato: %s', async (_caso, body) => {
    server.use(http.get(`${API}/me`, () => HttpResponse.json(body)))

    await expect(fetchMe()).rejects.toThrow()
  })

  it('propaga o 401 sem sessão', async () => {
    server.use(http.get(`${API}/me`, () => problem(401, 'UNAUTHENTICATED')))

    await expect(fetchMe()).rejects.toMatchObject({ status: 401 })
  })
})

describe('fetchSessions', () => {
  it('lê a lista de sessões', async () => {
    const item = {
      id: SESSION_ID,
      createdAt: '2026-10-14T10:00:00Z',
      lastSeenAt: '2026-10-15T09:30:00Z',
      current: true,
      description: 'Safari no macOS',
    }
    server.use(http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [item] })))

    await expect(fetchSessions()).resolves.toEqual([item])
  })

  it('rejeita data fora do formato RFC 3339 em UTC', async () => {
    server.use(
      http.get(`${API}/me/sessions`, () =>
        HttpResponse.json({
          items: [{ id: SESSION_ID, createdAt: 'ontem', lastSeenAt: 'hoje', current: false, description: 'x' }],
        }),
      ),
    )

    await expect(fetchSessions()).rejects.toThrow()
  })
})

describe('mutações de sessão', () => {
  it('logout renova o CSRF e manda o token no header', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    let sentToken: string | null = null
    server.use(
      http.post(`${API}/auth/logout`, ({ request }) => {
        sentToken = request.headers.get('X-XSRF-TOKEN')
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await logout()

    expect(sentToken).toBe(antiforgery.latestToken())
    expect(sentToken).not.toBeNull()
  })

  it('logout de uma sessão que já expirou (401) termina como saída concluída', async () => {
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => problem(401, 'UNAUTHENTICATED')))

    await expect(logout()).resolves.toBeUndefined()
  })

  it('logout propaga outras falhas', async () => {
    useAntiforgeryEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => problem(500, 'INTERNAL')))

    await expect(logout()).rejects.toBeInstanceOf(ApiProblemError)
  })

  it('revoga a sessão pelo id, codificado no caminho, com CSRF', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    let revoked: { id: string; token: string | null } | null = null
    server.use(
      http.delete(`${API}/me/sessions/:id`, ({ params, request }) => {
        revoked = { id: String(params.id), token: request.headers.get('X-XSRF-TOKEN') }
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await revokeSession('a/b')

    expect(revoked).toEqual({ id: 'a/b', token: antiforgery.latestToken() })
  })
})
