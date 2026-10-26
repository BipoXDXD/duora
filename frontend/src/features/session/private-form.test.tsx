import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { useForm } from 'react-hook-form'
import { expect, it } from 'vitest'
import { createQueryClient } from '@/app/query-client'
import { apiClient } from '@/shared/api'
import { API, server } from '@/test/msw'
import { installTestMe } from '@/test/private-context'
import { PrivateOperationProvider } from './PrivateOperationProvider'
import { usePrivateOperation } from './private-operation'
import { usePrivateMutation } from './private-mutation'

it('o resolver assíncrono conserva a operação anterior: trocar contexto emite zero POST', async () => {
  let release!: () => void
  let started!: () => void
  const validation = new Promise<void>((resolve) => {
    release = resolve
  })
  const entered = new Promise<void>((resolve) => {
    started = resolve
  })
  let writes = 0
  server.use(
    http.post(`${API}/me/profile`, () => {
      writes += 1
      return new HttpResponse(null, { status: 204 })
    }),
  )
  const me = installTestMe({
    id: '00000000-0000-4000-8000-000000000001',
    email: 'synthetic@example.test',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
  })
  let settled!: Promise<void>
  function Form() {
    const privateOperation = usePrivateOperation()
    const form = useForm<{ draft: string }>({
      defaultValues: { draft: 'draft-A' },
      resolver: async (values) => {
        started()
        await validation
        return { values, errors: {} }
      },
    })
    const save = usePrivateMutation({
      mutationFn: (operation, value: string) =>
        apiClient.sendCommand({ operation, method: 'POST', path: '/api/v1/me/profile', body: { draft: value } }),
    })
    return (
      <form
        aria-label="test-form"
        onSubmit={(event) => {
          settled = privateOperation
            .run((operation) =>
              form.handleSubmit(async (values) => {
                await save.mutateWithOperation(operation, values.draft)
              })(event),
            )
            .catch(() => undefined)
        }}
      >
        <button>save</button>
      </form>
    )
  }
  const cache = createQueryClient()
  render(
    <QueryClientProvider client={cache}>
      <PrivateOperationProvider owner={me.owner}>
        <Form />
      </PrivateOperationProvider>
    </QueryClientProvider>,
  )
  fireEvent.submit(screen.getByRole('form', { name: 'test-form' }))
  await entered
  apiClient.sessionContext.protect('identity')
  release()
  await settled
  expect(writes).toBe(0)
  expect(cache.getMutationCache().getAll()).toHaveLength(0)
  cache.clear()
})
