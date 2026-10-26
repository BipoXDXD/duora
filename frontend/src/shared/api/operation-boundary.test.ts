// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  createSessionContextStore,
  type OperationHandle,
  type SessionContextStore,
  type ValidatedBootstrap,
} from '../session-context'
import { createApiClient, type ApiClient, type ApiFetch } from './api-client'
import { ApiResponseError } from './api-errors'
import { sendWithFreshAntiforgeryToken } from './antiforgery-refresh'

const ORIGIN = 'https://duora.test'
const BINDING: ValidatedBootstrap = {
  userId: '00000000-0000-4000-8000-000000000001',
  sessionId: '00000000-0000-4000-8000-000000000011',
  phase: 'active',
  requestToken: 'private-A',
  authView: { status: 'Active', emailConfirmed: true, mfaEnabled: false, roles: ['participant'] },
}

function activeOperation(store: SessionContextStore) {
  const owner = store.acceptBootstrap(store.beginBootstrap({ kind: 'automatic' }), BINDING)
  store.completeOwnerInstallation(store.getSnapshot().transitionEpoch, owner)
  return store.begin(owner)
}

function clientFor(store: SessionContextStore, fetch: ApiFetch) {
  return createApiClient({
    sessionContext: store,
    fetch,
    origin: ORIGIN,
    readAntiforgeryToken: () => 'public-B',
    readLocale: () => 'pt-BR',
    now: () => 0,
  })
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

function heldBody(status: number, body: unknown) {
  const started = deferred<undefined>()
  let controller!: ReadableStreamDefaultController<Uint8Array>
  let canceled = false
  const stream = new ReadableStream<Uint8Array>(
    {
      start(value) {
        controller = value
      },
      pull() {
        started.resolve(undefined)
      },
      cancel() {
        canceled = true
      },
    },
    { highWaterMark: 0 },
  )
  return {
    started: started.promise,
    response: new Response(stream, {
      status,
      headers: { 'Content-Type': status === 200 ? 'application/json' : 'application/problem+json' },
    }),
    finish() {
      if (!canceled) {
        controller.enqueue(new TextEncoder().encode(JSON.stringify(body)))
        controller.close()
      }
    },
  }
}

function rawTransport(client: ApiClient, operation: OperationHandle): ApiFetch {
  return client.fetchFor(operation)
}

describe('fronteira real do cliente HTTP', () => {
  it.each(['GET', 'PUT'] as const)('pina a prova privada em %s apesar do cookie público B', async (method) => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const requests: Request[] = []
    const client = clientFor(store, (request) => {
      requests.push(request)
      return Promise.resolve(new Response('{"version":"1"}', { headers: { 'Content-Type': 'application/json' } }))
    })

    await client.requestJson({
      method,
      path: '/api/v1/me/time-zone',
      operation,
      ...(method === 'PUT' ? { body: { timeZone: 'America/Sao_Paulo' } } : {}),
    })

    expect(requests[0]?.headers.get('X-XSRF-TOKEN')).toBe('private-A')
    expect(requests[0]?.redirect).toBe('error')
    store.finish(operation)
  })

  it('nenhuma leitura privada sem operação chega ao transporte', async () => {
    const store = createSessionContextStore()
    const requests: Request[] = []
    const client = clientFor(store, (request) => {
      requests.push(request)
      return Promise.resolve(new Response('{}', { headers: { 'Content-Type': 'application/json' } }))
    })

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/me/time-zone' }).then(
      () => null,
      (value: unknown) => value as Error,
    )

    expect(error?.name).toBe('AbortError')
    expect(requests).toHaveLength(0)
  })

  it.each([200, 503])('descarta corpo tardio %i sem sucesso ou erro reaproveitável', async (status) => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const body = heldBody(
      status,
      status === 200 ? { privateValue: 'from-A' } : { title: 'Temporarily unavailable', status },
    )
    const client = clientFor(store, () => Promise.resolve(body.response))
    const pending = client.requestJson({ method: 'GET', path: '/api/v1/me/time-zone', operation }).then(
      (value) => ({ value, error: null }),
      (error: unknown) => ({ value: null, error: error as Error }),
    )
    await body.started

    store.protect('identity')
    body.finish()
    const result = await pending

    expect(result.error?.name).toBe('AbortError')
    expect(result.value).toBeNull()
  })

  it('um clone de resposta bruta também perde acesso ao corpo após proteção', async () => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const client = clientFor(store, () =>
      Promise.resolve(
        new Response('{"privateValue":"from-A"}', {
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    const response = await rawTransport(client, operation)(new Request(`${ORIGIN}/api/v1/me/time-zone`))
    const clone = response.clone()

    store.protect('context')

    await expect(clone.json()).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('uma prova privada não é reaproveitada em rota de autenticação pública', async () => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const requests: Request[] = []
    const client = clientFor(store, (request) => {
      requests.push(request)
      return Promise.resolve(new Response(null, { status: 204 }))
    })

    const error = await client.sendCommand({ method: 'POST', path: '/api/v1/auth/login', operation }).then(
      () => null,
      (value: unknown) => value as Error,
    )

    expect(error?.name).toBe('AbortError')
    expect(requests).toHaveLength(0)
  })

  it('não envia POST público antigo se a identidade mudar durante a obtenção de CSRF', async () => {
    const store = createSessionContextStore()
    const started = deferred<undefined>()
    const release = deferred<undefined>()
    const methods: string[] = []
    const client = clientFor(store, async (request) => {
      methods.push(request.method)
      if (request.method === 'GET') {
        started.resolve(undefined)
        await release.promise
      }
      return new Response(null, { status: 204 })
    })
    const pending = sendWithFreshAntiforgeryToken(client, {
      method: 'POST',
      path: '/api/v1/auth/login',
      body: { email: 'a@example.com' },
    }).catch((error: unknown) => error as Error)
    await started.promise

    store.protect('identity')
    release.resolve(undefined)
    const result = await pending

    expect(result?.name).toBe('AbortError')
    expect(methods).toEqual(['GET'])
  })

  it('JSON malformado não fica no objeto de erro', async () => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const client = clientFor(store, () =>
      Promise.resolve(new Response('private-body-A', { headers: { 'Content-Type': 'application/json' } })),
    )

    const error = await client.requestJson({ method: 'GET', path: '/api/v1/me/time-zone', operation }).then(
      () => null,
      (value: unknown) => value as Error,
    )

    expect(error).toBeInstanceOf(ApiResponseError)
    expect(error?.message).not.toContain('private-body-A')
  })
  it('a entrada bruta sem capacidade não adquire o dono ativo', async () => {
    const store = createSessionContextStore()
    activeOperation(store)
    let calls = 0
    const client = clientFor(store, () => {
      calls += 1
      return Promise.resolve(new Response('{}'))
    })
    await expect(client.fetch(new Request(`${ORIGIN}/api/v1/me/time-zone`))).rejects.toMatchObject({
      name: 'AbortError',
    })
    expect(calls).toBe(0)
  })

  it('leitor dedicado aceita 403 tipado sem instalar dono nem transmitir prova privada', async () => {
    const store = createSessionContextStore()
    const body = { code: 'PASSWORD_CHANGE_REQUIRED', sessionContext: { ...BINDING, phase: 'password-change' } }
    let observed: Request | undefined
    const client = clientFor(store, (request) => {
      observed = request
      return Promise.resolve(
        new Response(JSON.stringify(body), { status: 403, headers: { 'Content-Type': 'application/problem+json' } }),
      )
    })
    const ticket = store.beginBootstrap({ kind: 'automatic' })
    const result = await client.readMeBootstrap(ticket)
    expect(result).toEqual({ status: 403, body })
    expect(observed?.headers.has('X-XSRF-TOKEN')).toBe(false)
    expect(observed?.redirect).toBe('error')
    expect(store.getSnapshot()).toMatchObject({ owner: null, pendingOwner: null })
    store.finishBootstrap(ticket)
  })

  it('proteção durante o corpo de Me cancela bootstrap sem conservar o conteúdo', async () => {
    const store = createSessionContextStore()
    const body = heldBody(200, { sessionContext: BINDING })
    const client = clientFor(store, () => Promise.resolve(body.response))
    const ticket = store.beginBootstrap({ kind: 'automatic' })
    const pending = client.readMeBootstrap(ticket).catch((error: unknown) => error as Error)
    await body.started
    store.protect('identity')
    body.finish()
    expect(await pending).toMatchObject({ name: 'AbortError' })
    expect(store.getSnapshot().owner).toBeNull()
    store.finishBootstrap(ticket)
  })

  it('JSON de bootstrap inválido não vaza no erro nem em sua causa', async () => {
    const store = createSessionContextStore()
    const client = clientFor(store, () =>
      Promise.resolve(new Response('private-proof-secret', { headers: { 'Content-Type': 'application/json' } })),
    )
    const ticket = store.beginBootstrap({ kind: 'automatic' })
    const error = await client.readMeBootstrap(ticket).catch((value: unknown) => value as Error)
    expect(error).toBeInstanceOf(ApiResponseError)
    expect(JSON.stringify(error)).not.toContain('private-proof-secret')
    expect(error).not.toHaveProperty('cause')
    store.finishBootstrap(ticket)
  })
  it('falha de transporte após trocar identidade também vira AbortError', async () => {
    const store = createSessionContextStore()
    const operation = activeOperation(store)
    const started = deferred<undefined>()
    const release = deferred<undefined>()
    const client = clientFor(store, async () => {
      started.resolve(undefined)
      await release.promise
      throw new TypeError('offline')
    })
    const pending = client
      .requestJson({ method: 'GET', path: '/api/v1/me/time-zone', operation })
      .catch((error: unknown) => error as Error)
    await started.promise
    store.protect('identity')
    release.resolve(undefined)
    expect(await pending).toMatchObject({ name: 'AbortError' })
  })
})

// Browser response.body can be a stream even for 204: https://developer.mozilla.org/en-US/docs/Web/API/Response/body
describe('respostas nativas sem conteúdo com stream exposto pelo navegador', () => {
  it.each([204, 205])('aceita o status %i sem reconstruir um corpo proibido', async (status) => {
    const store = createSessionContextStore()
    let canceled = 0
    const response = new Response(null, { status })
    Object.defineProperty(response, 'body', {
      value: new ReadableStream<Uint8Array>(
        {
          cancel() {
            canceled += 1
          },
        },
        { highWaterMark: 0 },
      ),
    })
    const client = clientFor(store, () => Promise.resolve(response))
    await expect(client.sendCommand({ method: 'GET', path: '/api/v1/auth/csrf' })).resolves.toBeUndefined()
    expect(canceled).toBe(1)
  })

  it('leva o mesmo gesto de CSRF204 até o login204 no comportamento do navegador', async () => {
    const store = createSessionContextStore()
    const paths: string[] = []
    let canceled = 0
    const client = clientFor(store, (request) => {
      paths.push(new URL(request.url).pathname)
      const response = new Response(null, { status: 204 })
      Object.defineProperty(response, 'body', {
        value: new ReadableStream<Uint8Array>(
          {
            cancel() {
              canceled += 1
            },
          },
          { highWaterMark: 0 },
        ),
      })
      return Promise.resolve(response)
    })
    await expect(
      sendWithFreshAntiforgeryToken(client, {
        method: 'POST',
        path: '/api/v1/auth/login',
        body: { email: 'synthetic@example.test', password: 'synthetic' },
      }),
    ).resolves.toBeUndefined()
    expect(paths).toEqual(['/api/v1/auth/csrf', '/api/v1/auth/login'])
    expect(canceled).toBe(2)
  })
})

it('uma resposta304 com stream continua sendo recusa tipada, com descarte do corpo', async () => {
  const store = createSessionContextStore()
  let canceled = false
  const response = new Response(null, { status: 304 })
  Object.defineProperty(response, 'body', {
    value: new ReadableStream<Uint8Array>(
      {
        cancel() {
          canceled = true
        },
      },
      { highWaterMark: 0 },
    ),
  })
  const client = clientFor(store, () => Promise.resolve(response))
  await expect(client.sendCommand({ method: 'GET', path: '/api/v1/auth/csrf' })).rejects.toBeInstanceOf(
    ApiResponseError,
  )
  expect(canceled).toBe(true)
})

it('a normalização204 não permite ler ou clonar uma conclusão depois da proteção', async () => {
  const store = createSessionContextStore()
  const operation = activeOperation(store)
  const response = new Response(null, { status: 204 })
  Object.defineProperty(response, 'body', { value: new ReadableStream<Uint8Array>() })
  const client = clientFor(store, () => Promise.resolve(response))
  const owned = await client.fetchFor(operation)(new Request(`${ORIGIN}/api/v1/me/profile`))
  store.protect('identity')
  await expect(owned.text()).rejects.toMatchObject({ name: 'AbortError' })
  expect(() => owned.clone()).toThrow(expect.objectContaining({ name: 'AbortError' }))
})
