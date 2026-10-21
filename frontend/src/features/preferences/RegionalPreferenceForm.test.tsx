import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

function useBackend(locale: 'pt-BR' | 'en-US' = 'pt-BR') {
  const state = { mode: 'pending', countryCodes: [] as string[], version: 0 }
  const writes: unknown[] = []
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json({
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
        email: 'a@example.com',
        status: 'Active',
        emailConfirmed: true,
        mfaEnabled: false,
        roles: ['participant'],
        preferredLocale: locale,
      }),
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
          ? /Salvar aqui ainda não restringe os pareamentos atuais/
          : /Saving here does not yet restrict current matches/,
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
    expect(api.csrf.calls()).toBeGreaterThan(0)
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
