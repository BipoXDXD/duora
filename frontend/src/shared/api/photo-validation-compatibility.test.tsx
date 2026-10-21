import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import observations from '../../../../docs/testing/evidence/adr-0034/observations.json'
import { renderWithI18n } from '@/test/render'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { createApiClient, type ApiClientDependencies } from './api-client'
import { ApiProblemError } from './api-errors'
import { classifyRequestFailure } from './request-failure'

const HEADER = 'Duora-Validation-Format'
const CANARY = '<img src=x onerror="window.CANARY=1">REMOTE_UPLOAD_CANARY'
const capturedLegacy = observations.responses.filter((entry) => entry.status === 400)
const base = {
  type: 'urn:duora:problem:validation-failed',
  title: CANARY,
  detail: CANARY,
  code: 'VALIDATION_FAILED',
  status: 400,
  traceId: 'synthetic-correlation',
}
// O transporte abaixo é um mock explícito. Os produtores reais são cobertos nos testes Kestrel/Caddy.
interface Example {
  readonly name: string
  readonly body: Readonly<Record<string, unknown>>
  readonly optIn: boolean
  readonly confirmation: string | null
}

const examples: readonly Example[] = [
  ...capturedLegacy.map((entry) => ({
    name: `servidor anterior: ${entry.name}`,
    body: entry.body,
    optIn: entry.name === 'body-empty-optin-ignored',
    confirmation: null,
  })),
  {
    name: 'novo/body',
    body: { ...base, errors: { body: ['PHOTO_BODY_EMPTY'] } },
    optIn: true,
    confirmation: 'codes-v1',
  },
  {
    name: 'novo/key',
    body: { ...base, errors: { 'Idempotency-Key': ['IDEMPOTENCY_KEY_INVALID'] } },
    optIn: true,
    confirmation: 'codes-v1',
  },
  {
    name: 'código sem confirmação',
    body: { ...base, errors: { body: ['PHOTO_BODY_EMPTY'] } },
    optIn: true,
    confirmation: null,
  },
  {
    name: 'confirmação desconhecida',
    body: { ...base, errors: { body: ['PHOTO_BODY_EMPTY'] } },
    optIn: true,
    confirmation: 'unknown',
  },
  {
    name: 'campo desconhecido',
    body: { ...base, errors: { constructor: [CANARY] } },
    optIn: true,
    confirmation: 'codes-v1',
  },
  {
    name: 'array múltiplo',
    body: { ...base, errors: { body: ['PHOTO_BODY_EMPTY', CANARY] } },
    optIn: true,
    confirmation: 'codes-v1',
  },
]

for (const locale of ['pt-BR', 'en-US'] as const) {
  describe(`compatibilidade de erros de foto / ${locale}`, () => {
    it.each(examples)('$name mantém parsing e fallback local sem retry', async ({ body, optIn, confirmation }) => {
      const fetch = vi.fn<ApiClientDependencies['fetch']>(() => {
        const headers = new Headers({ 'Content-Type': 'application/problem+json' })
        if (confirmation !== null) headers.set(HEADER, confirmation)
        return Promise.resolve(new Response(JSON.stringify(body), { status: 400, headers }))
      })
      const client = clientFor(fetch, locale)
      const error: unknown = await client
        .requestJson({
          method: 'POST',
          path: '/api/v1/me/photos',
          headers: optIn ? { [HEADER]: 'codes-v1' } : {},
        })
        .catch((caught: unknown) => caught)
      expect(error).toBeInstanceOf(ApiProblemError)
      if (!(error instanceof ApiProblemError)) throw new Error('Expected typed Problem Details')
      expect(error.problem.errors).toEqual(body.errors)
      expect(error.problem.code).toBe('VALIDATION_FAILED')
      expect(fetch).toHaveBeenCalledTimes(1)
      const request = fetch.mock.calls[0]?.[0]
      expect(request?.headers.get(HEADER)).toBe(optIn ? 'codes-v1' : null)
      expect(request?.headers.get('X-XSRF-TOKEN')).toBe('synthetic-csrf-token')
      expect(request?.credentials).toBe('same-origin')
      renderWithI18n(<RequestFailureAlert failure={classifyRequestFailure(error)} />, locale)
      expect(screen.getByRole('alert')).toHaveTextContent(
        locale === 'pt-BR'
          ? 'Algo deu errado do nosso lado. Tente de novo em instantes.'
          : 'Something went wrong on our side. Please try again in a moment.',
      )
      expect(document.body.innerHTML).not.toContain('REMOTE_UPLOAD_CANARY')
      expect(document.body.innerHTML).not.toContain('PHOTO_BODY_EMPTY')
      expect(document.body.innerHTML).not.toContain('IDEMPOTENCY_KEY_INVALID')
      expect(document.body.innerHTML).not.toContain('A foto está vazia.')
      expect(fetch).toHaveBeenCalledTimes(1)
    })
  })
}

it('a suspensão de adesão e o rollback do servidor preservam a chave e não causam retry automático', async () => {
  const prior = capturedLegacy.find((entry) => entry.name === 'body-empty-optin-ignored')
  if (prior === undefined) throw new Error('Missing captured old-server response')
  const bodies: readonly Readonly<Record<string, unknown>>[] = [
    { ...base, errors: { body: ['PHOTO_BODY_EMPTY'] } },
    prior.body,
    prior.body,
  ]
  let index = 0
  const fetch = vi.fn<ApiClientDependencies['fetch']>(() => {
    const headers = new Headers({ 'Content-Type': 'application/problem+json' })
    if (index === 0) headers.set(HEADER, 'codes-v1')
    return Promise.resolve(new Response(JSON.stringify(bodies[index++]), { status: 400, headers }))
  })
  const client = clientFor(fetch)
  for (let action = 0; action < 3; action++) {
    const headers: Record<string, string> = { 'Idempotency-Key': 'unchanged-synthetic-key' }
    if (action < 2) headers[HEADER] = 'codes-v1'
    const error: unknown = await client
      .requestJson({ method: 'POST', path: '/api/v1/me/photos', headers })
      .catch((caught: unknown) => caught)
    expect(error).toBeInstanceOf(ApiProblemError)
    if (!(error instanceof ApiProblemError)) throw new Error('Expected Problem Details')
    expect(error.problem.errors).toEqual(bodies[action]?.errors)
    expect(classifyRequestFailure(error)).toEqual({ kind: 'problem', status: 400, code: 'VALIDATION_FAILED' })
    expect(fetch).toHaveBeenCalledTimes(action + 1)
  }
  expect(fetch.mock.calls.map(([request]) => request.headers.get(HEADER))).toEqual(['codes-v1', 'codes-v1', null])
  expect(fetch.mock.calls.map(([request]) => request.headers.get('Idempotency-Key'))).toEqual([
    'unchanged-synthetic-key',
    'unchanged-synthetic-key',
    'unchanged-synthetic-key',
  ])
})

it('um POST aceito sem confirmação de formato não é reenviado para descobrir capacidade do servidor', async () => {
  const accepted = { photoId: 'synthetic-photo-id', status: 'Quarantined' }
  const fetch = vi.fn<ApiClientDependencies['fetch']>(() =>
    Promise.resolve(
      new Response(JSON.stringify(accepted), {
        status: 202,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  )
  const client = clientFor(fetch)
  await expect(
    client.requestJson({ method: 'POST', path: '/api/v1/me/photos', headers: { [HEADER]: 'codes-v1' } }),
  ).resolves.toEqual(accepted)
  expect(fetch).toHaveBeenCalledTimes(1)
})

function clientFor(fetch: ApiClientDependencies['fetch'], locale: 'pt-BR' | 'en-US' = 'pt-BR') {
  return createApiClient({
    fetch,
    origin: 'https://duora.test',
    readAntiforgeryToken: () => 'synthetic-csrf-token',
    readLocale: () => locale,
    now: () => Date.parse('2026-10-21T12:00:00Z'),
  })
}
