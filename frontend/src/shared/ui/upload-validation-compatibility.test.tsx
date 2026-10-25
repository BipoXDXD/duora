import { syntheticPrivateOperation } from '@/test/owned-api-context'
import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createApiClient } from '@/shared/api/api-client'
import { ApiProblemError } from '@/shared/api/api-errors'
import { classifyRequestFailure } from '@/shared/api/request-failure'
import type { paths } from '@/shared/api/schema'
import { renderWithI18n } from '@/test/render'
import { RequestFailureAlert } from './alert'

type UploadHeaders = paths['/api/v1/me/photos']['post']['parameters']['header']
const CANARY = '<img src=x onerror="window.CANARY=1">CANARY SQL password'
const cases = [
  ['new server / legacy client', false, null, 'A foto está vazia.'],
  ['new server / explicit request', true, 'codes-v1', 'PHOTO_BODY_EMPTY'],
  ['old server / explicit request', true, null, 'A foto está vazia.'],
  ['invalid confirmation', true, 'CODES-V1', 'PHOTO_BODY_EMPTY'],
  ['unknown field code', true, 'codes-v1', 'constructor'],
  ['untrusted remote text', true, 'codes-v1', CANARY],
] as const

for (const locale of ['pt-BR', 'en-US'] as const) {
  describe(`upload compatibility in ${locale}`, () => {
    it.each(cases.map(([name, optIn, confirmation, fieldValue]) => ({ name, optIn, confirmation, fieldValue })))(
      '$name keeps the existing local fallback without retrying POST',
      async ({ optIn, confirmation, fieldValue }) => {
        const fetch = vi.fn<(request: Request) => Promise<Response>>(() =>
          Promise.resolve(
            new Response(
              JSON.stringify({
                type: 'urn:duora:problem:validation-failed',
                status: 400,
                code: 'VALIDATION_FAILED',
                title: CANARY,
                detail: CANARY,
                errors: { body: [fieldValue] },
              }),
              {
                status: 400,
                headers: {
                  'Content-Type': 'application/problem+json',
                  ...(confirmation === null ? {} : { 'Duora-Validation-Format': confirmation }),
                },
              },
            ),
          ),
        )
        const client = createApiClient({
          fetch,
          origin: 'https://duora.test',
          readAntiforgeryToken: () => 'synthetic',
          readLocale: () => locale,
          now: () => 0,
        })
        // Compile against the generated optional request header without adopting it in a product flow.
        const headers: UploadHeaders = {
          'Idempotency-Key': 'synthetic',
          ...(optIn ? { 'Duora-Validation-Format': 'codes-v1' } : {}),
        }
        const request = new Request('https://duora.test/api/v1/me/photos', {
          method: 'POST',
          headers,
          body: new Uint8Array(0),
        })
        const operation = syntheticPrivateOperation(client.sessionContext)
        const error: unknown = await client
          .fetchFor(operation)(request)
          .catch((failure: unknown) => failure)
        expect(error).toBeInstanceOf(ApiProblemError)
        expect(error).toMatchObject({ problem: { errors: { body: [fieldValue] } } })
        expect(fetch).toHaveBeenCalledTimes(1)
        expect(fetch.mock.calls[0]?.[0].headers.get('Duora-Validation-Format')).toBe(optIn ? 'codes-v1' : null)
        renderWithI18n(<RequestFailureAlert failure={classifyRequestFailure(error)} />, locale)
        expect(screen.getByRole('alert')).toHaveTextContent(
          locale === 'pt-BR' ? 'Algo deu errado do nosso lado.' : 'Something went wrong on our side.',
        )
        expect(document.body.innerHTML).not.toContain('PHOTO_BODY_EMPTY')
        expect(document.body.innerHTML).not.toContain('A foto está vazia.')
        expect(document.body.innerHTML).not.toContain('CANARY')
      },
    )
  })
}
