import { withSessionContext } from '@/test/private-context'
import { act, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

function useBackend(locale: 'pt-BR' | 'en-US' = 'pt-BR') {
  const state = { mode: 'pending', countryCodes: [] as string[], version: 0 }
  const writes: unknown[] = []
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(
        withSessionContext({
          id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
          email: 'a@example.com',
          status: 'Active',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
          preferredLocale: locale,
        }),
      ),
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
    http.get(`${API}/me/time-zone`, () => HttpResponse.json({ timeZone: null, version: '0' })),
    http.get(`${API}/me/time-zone/options`, () => HttpResponse.json({ timeZones: ['UTC'] })),
    http.get(`${API}/me/regional-preferences`, () => HttpResponse.json({ ...state, version: String(state.version) })),
    http.put(`${API}/me/regional-preferences`, async ({ request }) => {
      const body = (await request.json()) as { mode: string; countryCodes: string[] }
      writes.push(body)
      if (request.headers.get('If-Match') !== `"${String(state.version)}"`) return problem(412, 'PRECONDITION_FAILED')
      Object.assign(state, body, { version: state.version + 1 })
      return HttpResponse.json({ ...state, version: String(state.version) })
    }),
  )
  server.use(http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: false })))
  const csrf = useAntiforgeryEndpoint()
  return { state, writes, csrf }
}

describe.each([
  {
    locale: 'pt-BR',
    mode: 'Preferência regional',
    countries: 'Somente países escolhidos',
    any: 'Sem restrição de país',
    save: 'Salvar preferência regional',
    saved: 'Preferência regional salva.',
    reload: 'Recarregar preferência regional',
    brazil: 'Brasil',
  },
  {
    locale: 'en-US',
    mode: 'Regional preference',
    countries: 'Selected countries only',
    any: 'No country restriction',
    save: 'Save regional preference',
    saved: 'Regional preference saved.',
    reload: 'Reload regional preference',
    brazil: 'Brazil',
  },
] as const)('manual regional choice / $locale', ({ locale, mode, countries, any, save, saved, reload, brazil }) => {
  it('starts pending and never saves an implicit unrestricted preference', async () => {
    const api = useBackend(locale)
    const { user } = await renderApp('/account/preferences')
    const field = await screen.findByRole('combobox', { name: mode })
    expect(field).toHaveValue('pending')
    expect(
      screen.getByText(
        locale === 'pt-BR'
          ? /Nas rodadas com filtro regional, a escolha de país é respeitada pelos dois participantes/
          : /In rounds with regional filtering, both participants.*country choices are respected/,
      ),
    ).toBeInTheDocument()
    expect(
      screen.getByText(
        locale === 'pt-BR'
          ? /Sem uma preferência salva, você não participa dessas rodadas/
          : /Without a saved preference, you cannot join these rounds/,
      ),
    ).toBeInTheDocument()
    expect(
      screen.getByText(
        locale === 'pt-BR'
          ? /Rodadas publicadas antes da ativação mantêm as regras anteriores/
          : /Rounds published before activation retain their previous rules/,
      ),
    ).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: save }))
    expect(api.writes).toEqual([])
    await user.selectOptions(field, countries)
    await user.click(screen.getByRole('button', { name: save }))
    expect(api.writes).toEqual([])
    await user.click(screen.getByRole('checkbox', { name: brazil }))
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByText(saved)
    expect(api.writes).toEqual([{ mode: 'countries', countryCodes: ['BR'] }])
    await user.selectOptions(field, any)
    await user.click(screen.getByRole('button', { name: save }))
    await waitFor(() => {
      expect(api.writes).toHaveLength(2)
    })
    expect(api.writes[1]).toEqual({ mode: 'unrestricted', countryCodes: [] })
    expect(api.csrf.calls()).toBe(0)
  })
  it('preserves the draft after 412 and only retries after explicit reload and save', async () => {
    const api = useBackend(locale)
    const { user } = await renderApp('/account/preferences')
    const field = await screen.findByRole('combobox', { name: mode })
    await user.selectOptions(field, countries)
    await user.click(screen.getByRole('checkbox', { name: brazil }))
    api.state.version++
    api.state.mode = 'unrestricted'
    await user.click(screen.getByRole('button', { name: save }))
    await user.click(await screen.findByRole('button', { name: reload }))
    expect(field).toHaveValue('countries')
    expect(screen.getByRole('checkbox', { name: brazil })).toBeChecked()
    expect(api.writes).toHaveLength(1)
    await user.click(screen.getByRole('button', { name: save }))
    await screen.findByText(saved)
    expect(api.writes[1]).toEqual({ mode: 'countries', countryCodes: ['BR'] })
  })
})

describe('optional country suggestion', () => {
  it('preselects only an untouched pending draft, requires a separate explicit save, and keeps languages unchanged', async () => {
    const api = useBackend()
    server.use(
      http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
      http.post(`${API}/me/region-detection`, () =>
        HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' }),
      ),
    )
    const { user } = await renderApp('/account/preferences')
    await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('countries'))
    expect(screen.getByRole('checkbox', { name: 'Brasil' })).toBeChecked()
    expect(api.writes).toEqual([])
    expect(screen.getByRole('checkbox', { name: 'Inglês' })).not.toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Salvar preferência regional' }))
    await screen.findByText('Preferência regional salva.')
    expect(api.writes).toEqual([{ mode: 'countries', countryCodes: ['BR'] }])
  })
  it('keeps a manual choice when detection finishes after editing, without disabling the manual fields', async () => {
    useBackend()
    let finish!: () => void
    const pending = new Promise<void>((resolve) => {
      finish = resolve
    })
    server.use(
      http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
      http.post(`${API}/me/region-detection`, async () => {
        await pending
        return HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' })
      }),
    )
    const { user } = await renderApp('/account/preferences')
    await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
    await user.selectOptions(screen.getByRole('combobox', { name: 'Preferência regional' }), 'unrestricted')
    await act(async () => {
      finish()
      await pending
    })
    await screen.findByText('Sua escolha manual foi mantida.')
    expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('unrestricted')
  })
  it('has no detection action while capability is disabled', async () => {
    useBackend()
    await renderApp('/account/preferences')
    await screen.findByRole('combobox', { name: 'Preferência regional' })
    expect(screen.queryByRole('button', { name: 'Sugerir país pela conexão' })).not.toBeInTheDocument()
  })
})

it.each(['unknown', 'unsupported', 'unavailable', 'disabled'])(
  'a %s result leaves pending unchanged and does not save',
  async (status) => {
    const api = useBackend()
    server.use(
      http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
      http.post(`${API}/me/region-detection`, () =>
        HttpResponse.json({ status, countryCode: null, observedAt: '2026-10-21T12:00:00Z' }),
      ),
    )
    const { user } = await renderApp('/account/preferences')
    await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
    await screen.findByText(/Escolha manualmente/)
    if (status === 'disabled')
      expect(screen.queryByRole('button', { name: 'Sugerir país pela conexão' })).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('pending')
    expect(api.writes).toEqual([])
  },
)

it('ignores A after B, discards a canceled observation and never changes a saved preference', async () => {
  const api = useBackend()
  let finishA!: () => void
  let startedA!: () => void
  const aStarted = new Promise<void>((resolve) => {
    startedA = resolve
  })
  const aPending = new Promise<void>((resolve) => {
    finishA = resolve
  })
  let calls = 0
  server.use(
    http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
    http.post(`${API}/me/region-detection`, async () => {
      calls++
      if (calls === 1) {
        startedA()
        await aPending
        return HttpResponse.json({ status: 'unsupported', countryCode: null, observedAt: '2026-10-21T12:00:00Z' })
      }
      return HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:01Z' })
    }),
  )
  const { user } = await renderApp('/account/preferences')
  const button = await screen.findByRole('button', { name: 'Sugerir país pela conexão' })
  await user.click(button)
  await aStarted
  await user.click(button)
  await screen.findByText('País sugerido pré-selecionado neste rascunho. Confira e salve para confirmar.')
  await act(async () => {
    finishA()
    await aPending
  })
  expect(screen.queryByText(/O país estimado/)).not.toBeInTheDocument()
  await user.selectOptions(screen.getByRole('combobox', { name: 'Preferência regional' }), 'unrestricted')
  await user.click(screen.getByRole('button', { name: 'Salvar preferência regional' }))
  await screen.findByText('Preferência regional salva.')
  await user.click(button)
  await screen.findByText('Sua escolha manual foi mantida.')
  expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('unrestricted')
  expect(api.writes).toEqual([{ mode: 'unrestricted', countryCodes: [] }])
})

it('cancel and navigation discard the observation and a new visit starts pending', async () => {
  const api = useBackend()
  let finish!: () => void
  let started!: () => void
  const began = new Promise<void>((resolve) => {
    started = resolve
  })
  const pending = new Promise<void>((resolve) => {
    finish = resolve
  })
  server.use(
    http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
    http.post(`${API}/me/region-detection`, async () => {
      started()
      await pending
      return HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' })
    }),
  )
  const view = await renderApp('/account/preferences')
  await view.user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
  await began
  await view.user.click(screen.getByRole('button', { name: 'Cancelar sugestão' }))
  view.unmount()
  await act(async () => {
    finish()
    await pending
  })
  await renderApp('/account/preferences')
  expect(await screen.findByRole('combobox', { name: 'Preferência regional' })).toHaveValue('pending')
  expect(api.writes).toEqual([])
})

it('an expired session on suggestion is revalidated instead of leaving the private form usable', async () => {
  useBackend()
  server.use(
    http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
    http.post(`${API}/me/region-detection`, () => {
      server.use(http.get(`${API}/me`, () => problem(401, 'AUTHENTICATION_REQUIRED')))
      return problem(401, 'AUTHENTICATION_REQUIRED')
    }),
  )
  const { user } = await renderApp('/account/preferences')
  await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
  await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Preferência regional' })).not.toBeInTheDocument())
})

it('never replaces a previously saved preference even when the current form was not edited', async () => {
  const api = useBackend()
  api.state.mode = 'unrestricted'
  api.state.version = 7
  server.use(
    http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
    http.post(`${API}/me/region-detection`, () =>
      HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' }),
    ),
  )
  const { user } = await renderApp('/account/preferences')
  await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
  await screen.findByText('Sua escolha manual foi mantida.')
  expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('unrestricted')
  expect(api.writes).toEqual([])
})

it('a capability failure removes the action and invalidates an in-flight observation', async () => {
  const api = useBackend()
  let finish!: () => void
  let started!: () => void
  const began = new Promise<void>((resolve) => {
    started = resolve
  })
  const pending = new Promise<void>((resolve) => {
    finish = resolve
  })
  server.use(
    http.get(`${API}/me/region-detection/capability`, () => HttpResponse.json({ enabled: true })),
    http.post(`${API}/me/region-detection`, async () => {
      started()
      await pending
      return HttpResponse.json({ status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' })
    }),
  )
  const { user, queryClient } = await renderApp('/account/preferences')
  await user.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
  await began
  server.use(http.get(`${API}/me/region-detection/capability`, () => problem(503, 'TEMPORARILY_UNAVAILABLE')))
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ['account', '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6', 'region-detection-capability'],
    })
  })
  await waitFor(() =>
    expect(screen.queryByRole('button', { name: 'Sugerir país pela conexão' })).not.toBeInTheDocument(),
  )
  await act(async () => {
    finish()
    await pending
  })
  expect(screen.getByRole('combobox', { name: 'Preferência regional' })).toHaveValue('pending')
  expect(api.writes).toEqual([])
})

it('saves US independently and both enabled countries without inferring Brazil', async () => {
  const api = useBackend()
  const { user } = await renderApp('/account/preferences')
  await user.selectOptions(await screen.findByRole('combobox', { name: 'Preferência regional' }), 'countries')
  await user.click(screen.getByRole('checkbox', { name: 'Estados Unidos' }))
  await user.click(screen.getByRole('button', { name: 'Salvar preferência regional' }))
  await waitFor(() => {
    expect(api.writes).toHaveLength(1)
  })
  expect(api.writes[0]).toEqual({ mode: 'countries', countryCodes: ['US'] })
  await user.click(screen.getByRole('checkbox', { name: 'Brasil' }))
  await user.click(screen.getByRole('button', { name: 'Salvar preferência regional' }))
  await waitFor(() => {
    expect(api.writes).toHaveLength(2)
  })
  expect(api.writes[1]).toEqual({ mode: 'countries', countryCodes: ['BR', 'US'] })
})
