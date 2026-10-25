import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'a@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
function useBackend(locale: 'pt-BR' | 'en-US') {
  const state = { timeZone: null as string | null, version: 0 }
  const writes: unknown[] = []
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext({ ...me, preferredLocale: locale }))),
    http.get(`${API}/me/regional-preferences`, () =>
      HttpResponse.json({ mode: 'pending', countryCodes: [], version: '0' }),
    ),
    http.get(`${API}/me/preferences`, () =>
      HttpResponse.json({
        communicationLanguages: [],
        allowDifferentDisplayLocales: false,
        declarationRequired: true,
        version: '0',
      }),
    ),
    http.get(`${API}/me/locale`, () => HttpResponse.json({ locale, version: '0' })),
    http.get(`${API}/me/time-zone`, () => HttpResponse.json({ ...state, version: String(state.version) })),
    http.get(`${API}/me/time-zone/options`, () =>
      HttpResponse.json({ timeZones: ['UTC', 'America/New_York', 'America/Sao_Paulo', 'Asia/Kolkata'] }),
    ),
    http.put(`${API}/me/time-zone`, async ({ request }) => {
      const body = (await request.json()) as { timeZone: string | null }
      writes.push(body)
      if (request.headers.get('If-Match') !== `"${String(state.version)}"`) return problem(412, 'PRECONDITION_FAILED')
      state.timeZone = body.timeZone
      state.version++
      return HttpResponse.json({ ...state, version: String(state.version) })
    }),
  )
  useAntiforgeryEndpoint()
  return { state, writes }
}

describe.each([
  {
    locale: 'pt-BR',
    label: 'Fuso horário da conta (opcional)',
    device: 'Usar fuso deste dispositivo',
    save: 'Salvar fuso horário',
    saved: 'Fuso horário salvo.',
    reload: 'Recarregar fuso atual',
  },
  {
    locale: 'en-US',
    label: 'Account time zone (optional)',
    device: 'Use this device’s time zone',
    save: 'Save time zone',
    saved: 'Time zone saved.',
    reload: 'Reload current time zone',
  },
] as const)('explicit account zone / $locale', ({ locale, label, device, save, saved, reload }) => {
  it('starts unset, validates, requires Save for device fill, saves and removes without declaring languages', async () => {
    const api = useBackend(locale)
    const { user, queryClient } = await renderApp('/account/preferences')
    const field = await screen.findByRole('combobox', { name: label })
    expect(field).toHaveValue('')
    await user.type(field, 'Mars/Olympus')
    await user.click(screen.getByRole('button', { name: save }))
    expect(field).toHaveAttribute('aria-invalid', 'true')
    expect(api.writes).toEqual([])
    await user.click(screen.getByRole('button', { name: device }))
    expect(field).not.toHaveValue('')
    expect(api.writes).toEqual([])
    await user.clear(field)
    await user.type(field, 'America/New_York')
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByText(saved)
    expect(api.writes).toEqual([{ timeZone: 'America/New_York' }])
    expect(queryClient.getQueryData(['me'])).toMatchObject({ preferredTimeZone: 'America/New_York' })
    await user.clear(field)
    await user.click(screen.getByRole('button', { name: save }))
    await waitFor(() => {
      expect(api.writes).toEqual([{ timeZone: 'America/New_York' }, { timeZone: null }])
    })
    expect(queryClient.getQueryData(['me'])).toMatchObject({ preferredTimeZone: null })
  })
  it('accepts a device alias and persists the server-supported IANA identifier', async () => {
    const api = useBackend(locale)
    const { user } = await renderApp('/account/preferences')
    await user.type(await screen.findByRole('combobox', { name: label }), 'Asia/Calcutta')
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByText(saved)
    expect(api.writes).toEqual([{ timeZone: 'Asia/Kolkata' }])
  })
  it('keeps a conflicting draft, reloads the version and saves only after another explicit action', async () => {
    const api = useBackend(locale)
    const { user } = await renderApp('/account/preferences')
    const field = await screen.findByRole('combobox', { name: label })
    await user.type(field, 'America/Sao_Paulo')
    api.state.timeZone = 'UTC'
    api.state.version++
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByRole('button', { name: reload })
    expect(field).toHaveValue('America/Sao_Paulo')
    expect(screen.getByRole('button', { name: save })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: reload }))
    await waitFor(() => {
      expect(screen.getByRole('button', { name: save })).toBeEnabled()
    })
    expect(field).toHaveValue('America/Sao_Paulo')
    expect(api.writes).toHaveLength(1)
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByText(saved)
    expect(api.writes).toHaveLength(2)
  })
})

it('does not apply a delayed save to another signed-in account', async () => {
  useBackend('pt-BR')
  let release: (() => void) | undefined
  const pending = new Promise<void>((resolve) => {
    release = resolve
  })
  let received = false
  server.use(
    http.put(`${API}/me/time-zone`, async () => {
      received = true
      await pending
      return HttpResponse.json({ timeZone: 'America/New_York', version: '1' })
    }),
  )
  const { user, queryClient } = await renderApp('/account/preferences')
  await user.type(await screen.findByRole('combobox', { name: 'Fuso horário da conta (opcional)' }), 'America/New_York')
  await user.click(screen.getByRole('button', { name: 'Salvar fuso horário' }))
  await waitFor(() => {
    expect(received).toBe(true)
  })
  queryClient.setQueryData(['me'], { ...me, id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7', preferredTimeZone: 'UTC' })
  release?.()
  await waitFor(() => {
    expect(
      queryClient
        .getMutationCache()
        .getAll()
        .every((mutation) => mutation.state.status !== 'pending'),
    ).toBe(true)
  })
  expect(queryClient.getQueryData(['me'])).toMatchObject({ preferredTimeZone: 'UTC' })
})
