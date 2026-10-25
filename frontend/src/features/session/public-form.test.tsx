import { login } from '@/features/auth/auth-api'
import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { useForm } from 'react-hook-form'
import { expect, it } from 'vitest'
import { createQueryClient } from '@/app/query-client'
import { apiClient } from '@/shared/api'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { runPublicOperation, usePublicMutation } from './public-operation'

it('intenção pública captura antes do resolver: proteger durante a validação emite zeroCSRF/POST', async () => {
  let release!: () => void
  let started!: () => void
  const validation = new Promise<void>((resolve) => {
    release = resolve
  })
  const entered = new Promise<void>((resolve) => {
    started = resolve
  })
  let writes = 0
  const csrf = useAntiforgeryEndpoint()
  server.use(
    http.post(`${API}/auth/login`, () => {
      writes += 1
      return new HttpResponse(null, { status: 204 })
    }),
  )
  let settled!: Promise<void>
  function Form() {
    const form = useForm<{ password: string }>({
      defaultValues: { password: 'synthetic-secret' },
      resolver: async (values) => {
        started()
        await validation
        return { values, errors: {} }
      },
    })
    const send = usePublicMutation({
      mutationFn: (operation) => login(operation, { email: 'synthetic@example.test', password: 'synthetic-secret' }),
    })
    return (
      <form
        aria-label="public-form"
        onSubmit={(event) => {
          settled = runPublicOperation((operation) =>
            form.handleSubmit(async () => {
              await send.mutateWithOperation(operation, undefined)
            })(event),
          ).catch(() => undefined)
        }}
      >
        <button>submit</button>
      </form>
    )
  }
  const cache = createQueryClient()
  render(
    <QueryClientProvider client={cache}>
      <Form />
    </QueryClientProvider>,
  )
  fireEvent.submit(screen.getByRole('form', { name: 'public-form' }))
  await entered
  apiClient.sessionContext.protect('identity')
  release()
  await settled
  expect(writes).toBe(0)
  expect(csrf.calls()).toBe(0)
  expect(cache.getMutationCache().getAll()).toHaveLength(0)
  cache.clear()
})
