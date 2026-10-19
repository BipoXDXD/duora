import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
function useBackend() {
  const stored = { version: 0, languages: [] as string[], optIn: false }
  const writes: unknown[] = []
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(me)),
    http.get(`${API}/me/preferences`, () =>
      HttpResponse.json({
        communicationLanguages: stored.languages,
        allowDifferentDisplayLocales: stored.optIn,
        declarationRequired: stored.version === 0,
        version: String(stored.version),
      }),
    ),
    http.get(`${API}/me/locale`, () => HttpResponse.json({ locale: 'pt-BR', version: '0' })),
    http.put(`${API}/me/preferences`, async ({ request }) => {
      const body = (await request.json()) as { communicationLanguages: string[]; allowDifferentDisplayLocales: boolean }
      writes.push(body)
      if (request.headers.get('If-Match') !== `"${String(stored.version)}"`) return problem(412, 'PRECONDITION_FAILED')
      stored.languages = body.communicationLanguages
      stored.optIn = body.allowDifferentDisplayLocales
      stored.version++
      return HttpResponse.json({ ...body, declarationRequired: false, version: String(stored.version) })
    }),
  )
  useAntiforgeryEndpoint()
  return {
    writes,
    concurrentUpdate() {
      stored.version++
      stored.languages = ['pt']
    },
  }
}

describe('private communication preferences', () => {
  it('requires a declaration, starts unchecked, and saves only explicit choices', async () => {
    const api = useBackend()
    const { user } = await renderApp('/conta/preferencias')
    const english = await screen.findByRole('checkbox', { name: 'Inglês' })
    expect(english).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: /Aceito pares/ })).not.toBeChecked()
    expect(screen.getByText(/Chat e voz ao vivo não têm tradução automática/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Salvar preferências' }))
    expect(api.writes).toHaveLength(0)
    await user.click(english)
    await user.click(screen.getByRole('button', { name: 'Salvar preferências' }))
    await screen.findByText('Preferências salvas.')
    expect(api.writes).toEqual([{ communicationLanguages: ['en'], allowDifferentDisplayLocales: false }])
  })
  it('keeps the draft on conflict and requires explicit reload before retry', async () => {
    const api = useBackend()
    const { user } = await renderApp('/conta/preferencias')
    await user.click(await screen.findByRole('checkbox', { name: 'Inglês' }))
    api.concurrentUpdate()
    await user.click(screen.getByRole('button', { name: 'Salvar preferências' }))
    await screen.findByText(/Outra aba alterou estas preferências/)
    expect(screen.getByRole('checkbox', { name: 'Inglês' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Revisar versão atual' }))
    await screen.findByText(/Versão atual carregada/)
    expect(screen.getByRole('checkbox', { name: 'Inglês' })).toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Salvar preferências' }))
    await waitFor(() => {
      expect(api.writes).toHaveLength(2)
    })
    await screen.findByText('Preferências salvas.')
  })
})

it('loads the account display locale and persists header changes without declaring communication languages', async () => {
  const api = useBackend()
  server.use(http.get(`${API}/me`, () => HttpResponse.json({ ...me, preferredLocale: 'en-US' })))
  let savedLocale: unknown
  server.use(
    http.put(`${API}/me/locale`, async ({ request }) => {
      savedLocale = await request.json()
      return HttpResponse.json({ locale: 'pt-BR', version: '1' })
    }),
  )
  const { user } = await renderApp('/conta/preferencias')
  await screen.findByRole('heading', { name: 'Communication preferences' })
  await user.click(screen.getByRole('button', { name: 'Português' }))
  await screen.findByRole('heading', { name: 'Preferências de comunicação' })
  expect(savedLocale).toEqual({ locale: 'pt-BR' })
  expect(api.writes).toHaveLength(0)
})

it('does not apply a delayed locale save to a different signed-in account', async () => {
  useBackend()
  let release: (() => void) | undefined
  const pending = new Promise<void>((resolve) => {
    release = resolve
  })
  let received = false
  server.use(
    http.put(`${API}/me/locale`, async () => {
      received = true
      await pending
      return HttpResponse.json({ locale: 'en-US', version: '1' })
    }),
  )
  const { user, queryClient } = await renderApp('/conta/preferencias')
  await user.selectOptions(await screen.findByRole('combobox', { name: 'Idioma da interface e conteúdo' }), 'en-US')
  await user.click(screen.getByRole('button', { name: 'Salvar idioma de exibição' }))
  await waitFor(() => {
    expect(received).toBe(true)
  })
  queryClient.setQueryData(['me'], { ...me, id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7', preferredLocale: 'pt-BR' })
  release?.()
  await waitFor(() => {
    expect(
      queryClient
        .getMutationCache()
        .getAll()
        .every((mutation) => mutation.state.status !== 'pending'),
    ).toBe(true)
  })
  expect(queryClient.getQueryData(['me'])).toMatchObject({ preferredLocale: 'pt-BR' })
})

it('does not save a header language choice after the account changes during its locale read', async () => {
  useBackend()
  const { user, queryClient } = await renderApp('/conta/preferencias')
  await screen.findByRole('combobox', { name: 'Idioma da interface e conteúdo' })
  let release: (() => void) | undefined
  const pending = new Promise<void>((resolve) => {
    release = resolve
  })
  let received = false
  let writes = 0
  server.use(
    http.get(`${API}/me/locale`, async () => {
      received = true
      await pending
      return HttpResponse.json({ locale: 'pt-BR', version: '0' })
    }),
    http.put(`${API}/me/locale`, () => {
      writes++
      return HttpResponse.json({ locale: 'en-US', version: '1' })
    }),
  )
  await user.click(screen.getByRole('button', { name: 'English' }))
  await waitFor(() => {
    expect(received).toBe(true)
  })
  queryClient.setQueryData(['me'], { ...me, id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7', preferredLocale: 'pt-BR' })
  release?.()
  await waitFor(() => {
    expect(screen.getByRole('button', { name: 'English' })).toBeEnabled()
  })
  expect(writes).toBe(0)
})
