// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createApiClient, type ApiClientDependencies } from './api-client'
import { ApiNetworkError, ApiProblemError, ApiResponseError } from './api-errors'

const ORIGIN = 'https://duora.test'
const CSRF_TOKEN = 'token-antiforgery-123'
const NOW = Date.parse('2026-10-15T12:00:00Z')

type FakeFetch = ApiClientDependencies['fetch']

function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
    ...init,
  })
}

function problemResponse(body: unknown, status: number, contentType = 'application/problem+json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } })
}

function setup(
  respond: FakeFetch = () => Promise.resolve(jsonResponse({ ok: true })),
  token: string | null = CSRF_TOKEN,
) {
  const fetch = vi.fn<FakeFetch>(respond)
  const client = createApiClient({ fetch, origin: ORIGIN, readAntiforgeryToken: () => token, now: () => NOW })
  const sentRequest = (): Request => {
    const call = fetch.mock.calls[0]
    if (call === undefined) {
      throw new Error('fetch não foi chamado')
    }
    return call[0]
  }
  return { client, fetch, sentRequest }
}

describe('requestJson: sucesso', () => {
  it('devolve o corpo JSON de uma leitura bem-sucedida', async () => {
    const { client } = setup(() => Promise.resolve(jsonResponse({ id: 'e1', name: 'Noite de jogos' })))

    const body = await client.requestJson({ method: 'GET', path: '/api/v1/events/e1' })

    expect(body).toEqual({ id: 'e1', name: 'Noite de jogos' })
  })

  it('envia a requisição para a mesma origem com cookies same-origin', async () => {
    const { client, sentRequest } = setup()

    await client.requestJson({ method: 'GET', path: '/api/v1/me' })

    expect(sentRequest().url).toBe('https://duora.test/api/v1/me')
    expect(sentRequest().credentials).toBe('same-origin')
  })

  it('devolve undefined para 204 No Content', async () => {
    const { client } = setup(() => Promise.resolve(new Response(null, { status: 204 })))

    const body = await client.requestJson({ method: 'DELETE', path: '/api/v1/me/photos/p1' })

    expect(body).toBeUndefined()
  })

  it('serializa o corpo como JSON em mutações', async () => {
    const { client, sentRequest } = setup()

    await client.requestJson({ method: 'POST', path: '/api/v1/events/e1/check-in', body: { roundId: 'r1' } })

    expect(sentRequest().headers.get('Content-Type')).toBe('application/json')
    expect(await sentRequest().json()).toEqual({ roundId: 'r1' })
  })

  it('rejeita resposta 2xx com conteúdo que não é JSON', async () => {
    const { client } = setup(() =>
      Promise.resolve(new Response('<html></html>', { status: 200, headers: { 'Content-Type': 'text/html' } })),
    )

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/me' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiResponseError)
    expect(error).toMatchObject({ status: 200 })
  })
})

describe('sendCommand', () => {
  it.each([
    ['202 sem corpo', () => new Response(null, { status: 202 })],
    ['202 com texto', () => new Response('aceito', { status: 202, headers: { 'Content-Type': 'text/plain' } })],
    ['204', () => new Response(null, { status: 204 })],
  ])('conclui sem ler o corpo em %s', async (_caso, respond) => {
    const { client, sentRequest } = setup(() => Promise.resolve(respond()))

    await expect(
      client.sendCommand({ method: 'POST', path: '/api/v1/auth/register', body: { email: 'a@b.test' } }),
    ).resolves.toBeUndefined()
    expect(sentRequest().headers.get('X-XSRF-TOKEN')).toBe(CSRF_TOKEN)
    expect(await sentRequest().json()).toEqual({ email: 'a@b.test' })
  })

  it('rejeita com ApiProblemError quando o servidor recusa', async () => {
    const { client } = setup(() =>
      Promise.resolve(problemResponse({ title: 'Token inválido', code: 'INVALID_TOKEN' }, 400)),
    )

    const error = await client.sendCommand({ method: 'POST', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiProblemError)
    expect(error).toMatchObject({ status: 400, problem: { code: 'INVALID_TOKEN' } })
  })
})

describe('requestJson: antiforgery', () => {
  it.each(['POST', 'PUT', 'PATCH', 'DELETE'] as const)('anexa o header antiforgery em %s', async (method) => {
    const { client, sentRequest } = setup()

    await client.requestJson({ method, path: '/api/v1/resource' })

    expect(sentRequest().headers.get('X-XSRF-TOKEN')).toBe(CSRF_TOKEN)
  })

  it.each(['GET', 'HEAD', 'OPTIONS'] as const)('não anexa o header antiforgery em %s', async (method) => {
    const { client, sentRequest } = setup(() => Promise.resolve(new Response(null, { status: 204 })))

    await client.requestJson({ method, path: '/api/v1/resource' })

    expect(sentRequest().headers.has('X-XSRF-TOKEN')).toBe(false)
  })

  it('envia a mutação sem header quando ainda não há token, deixando o servidor recusar', async () => {
    const { client, sentRequest } = setup(undefined, null)

    await client.requestJson({ method: 'POST', path: '/api/v1/auth/login' })

    expect(sentRequest().headers.has('X-XSRF-TOKEN')).toBe(false)
  })
})

describe('requestJson: erros HTTP', () => {
  it('converte application/problem+json num ApiProblemError com os campos do contrato', async () => {
    const problem = {
      type: 'https://duora.test/problems/version-conflict',
      title: 'Versão desatualizada',
      status: 409,
      code: 'version_conflict',
      traceId: '00-abc-def-01',
      detail: 'O recurso mudou desde a última leitura.',
    }
    const { client } = setup(() => Promise.resolve(problemResponse(problem, 409)))

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiProblemError)
    expect(error).toMatchObject({ status: 409, problem })
  })

  it('aceita problem+json com parâmetro de charset e erros por campo', async () => {
    const problem = {
      type: 'about:blank',
      title: 'Dados inválidos',
      status: 400,
      code: 'validation_failed',
      errors: { displayName: ['too_long'] },
    }
    const { client } = setup(() =>
      Promise.resolve(problemResponse(problem, 400, 'application/problem+json; charset=utf-8')),
    )

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiProblemError)
    expect(error).toMatchObject({ problem: { errors: { displayName: ['too_long'] } } })
  })

  it('usa o status da resposta HTTP mesmo se o corpo declarar outro', async () => {
    const { client } = setup(() => Promise.resolve(problemResponse({ title: 'Conflito', status: 200 }, 409)))

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 409, problem: { status: 409, type: 'about:blank' } })
  })

  it('converte erro sem problem+json num ApiResponseError com o status', async () => {
    const { client } = setup(() =>
      Promise.resolve(new Response('Bad Gateway', { status: 502, headers: { 'Content-Type': 'text/plain' } })),
    )

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiResponseError)
    expect(error).toMatchObject({ status: 502 })
  })

  it.each([
    ['corpo que não é JSON', '{ quebrado'],
    ['JSON sem title', JSON.stringify({ status: 400 })],
    ['JSON que não é objeto', JSON.stringify(['erro'])],
  ])('trata problem+json inválido (%s) como ApiResponseError', async (_caso, body) => {
    const { client } = setup(() =>
      Promise.resolve(new Response(body, { status: 400, headers: { 'Content-Type': 'application/problem+json' } })),
    )

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiResponseError)
    expect(error).toMatchObject({ status: 400 })
  })
})

describe('requestJson: limite de uso', () => {
  it('expõe o Retry-After de um 429 com Problem Details em segundos', async () => {
    const { client } = setup(() =>
      Promise.resolve(
        new Response(JSON.stringify({ title: 'Muitas tentativas', status: 429, code: 'RATE_LIMITED' }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '30' },
        }),
      ),
    )

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/auth/login' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiProblemError)
    expect(error).toMatchObject({ status: 429, retryAfterSeconds: 30 })
  })

  it('expõe o Retry-After de um 429 sem Problem Details', async () => {
    const { client } = setup(() =>
      Promise.resolve(new Response('slow down', { status: 429, headers: { 'Retry-After': '5' } })),
    )

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/auth/login' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiResponseError)
    expect(error).toMatchObject({ status: 429, retryAfterSeconds: 5 })
  })

  it('usa null quando não há Retry-After', async () => {
    const { client } = setup(() => Promise.resolve(problemResponse({ title: 'Muitas tentativas' }, 429)))

    const error = await client.requestJson({ method: 'POST', path: '/api/v1/auth/login' }).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 429, retryAfterSeconds: null })
  })
})

describe('requestJson: rede e cancelamento', () => {
  it('converte falha de rede num ApiNetworkError preservando a causa', async () => {
    const cause = new TypeError('Failed to fetch')
    const { client } = setup(() => Promise.reject(cause))

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/x' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiNetworkError)
    expect(error).toMatchObject({ cause })
  })

  it('não repete automaticamente um POST que falhou na rede', async () => {
    const { client, fetch } = setup(() => Promise.reject(new TypeError('Failed to fetch')))

    await client.requestJson({ method: 'POST', path: '/api/v1/x' }).catch(() => undefined)

    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('propaga o cancelamento como AbortError, não como falha de rede', async () => {
    const controller = new AbortController()
    const { client } = setup(
      (request) =>
        new Promise<Response>((_resolve, reject) => {
          request.signal.addEventListener('abort', () => {
            reject(request.signal.reason as Error)
          })
        }),
    )

    const pending = client.requestJson({ method: 'GET', path: '/api/v1/x', signal: controller.signal })
    controller.abort()
    const error = await pending.catch((e: unknown) => e)

    expect(error).not.toBeInstanceOf(ApiNetworkError)
    expect(error).toMatchObject({ name: 'AbortError' })
  })
})

describe('fronteira same-origin', () => {
  it.each(['https://evil.example/api/v1/x', '//evil.example/api/v1/x', 'api/v1/x'])(
    'recusa o caminho %s sem chamar fetch',
    async (path) => {
      const { client, fetch } = setup()

      await expect(client.requestJson({ method: 'GET', path })).rejects.toThrow(/mesma origem/)
      expect(fetch).not.toHaveBeenCalled()
    },
  )

  it('fetch compatível com openapi-fetch aplica as mesmas regras a um Request pronto', async () => {
    const { client, sentRequest } = setup()

    const response = await client.fetch(new Request(`${ORIGIN}/api/v1/me`, { method: 'PATCH', credentials: 'include' }))

    expect(response.ok).toBe(true)
    expect(sentRequest().credentials).toBe('same-origin')
    expect(sentRequest().headers.get('X-XSRF-TOKEN')).toBe(CSRF_TOKEN)
  })

  it('fetch compatível recusa Request para outra origem', async () => {
    const { client, fetch } = setup()

    await expect(client.fetch(new Request('https://evil.example/api'))).rejects.toThrow(/mesma origem/)
    expect(fetch).not.toHaveBeenCalled()
  })
})
