import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import {
  authenticatedPage,
  expectSafeError,
  fulfillProblem,
  type Failure,
  type Locale,
} from './support/problem-canary.ts'

async function setup(page: Page, locale: Locale) {
  await authenticatedPage(page, locale)
  await page.route('**/api/v1/me/preferences', (route) =>
    route.fulfill({
      json: {
        communicationLanguages: [],
        allowDifferentDisplayLocales: false,
        declarationRequired: true,
        version: '0',
      },
    }),
  )
  await page.route('**/api/v1/me/locale', (route) => route.fulfill({ json: { locale, version: '0' } }))
  await page.route('**/api/v1/me/time-zone', (route) => route.fulfill({ json: { timeZone: null, version: '0' } }))
  await page.route('**/api/v1/me/time-zone/options', (route) => route.fulfill({ json: { timeZones: ['UTC'] } }))
  const state = {
    mode: 'pending',
    countryCodes: [] as string[],
    version: 0,
    failure: null as Failure | null,
    writes: [] as unknown[],
  }
  await page.route('**/api/v1/me/regional-preferences', async (route) => {
    if (route.request().method() === 'PUT') {
      expect(route.request().headers()['x-xsrf-token']).toBe('synthetic')
      const body = route.request().postDataJSON() as { mode: string; countryCodes: string[] }
      state.writes.push(body)
      if (state.failure !== null) {
        await fulfillProblem(route, state.failure)
        return
      }
      if (route.request().headers()['if-match'] !== `"${String(state.version)}"`) {
        await fulfillProblem(route, { status: 412, code: 'PRECONDITION_FAILED' })
        return
      }
      Object.assign(state, body, { version: state.version + 1 })
    }
    await route.fulfill({
      json: { mode: state.mode, countryCodes: state.countryCodes, version: String(state.version) },
    })
  })
  await page.goto('/account/preferences')
  return state
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`regional manual / ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    const label = pt ? 'Preferência regional' : 'Regional preference'
    const saveLabel = pt ? 'Salvar preferência regional' : 'Save regional preference'
    const brazil = pt ? 'Brasil' : 'Brazil'
    test('explicit country choice survives reload and leaves other resources untouched', async ({ page }) => {
      const state = await setup(page, locale)
      const field = page.getByRole('combobox', { name: label, exact: true })
      const save = page.getByRole('button', { name: saveLabel, exact: true })
      await expect(field).toHaveValue('pending')
      await save.click()
      expect(state.writes).toEqual([])
      await field.selectOption('countries')
      await save.click()
      expect(state.writes).toEqual([])
      await page.getByRole('checkbox', { name: brazil, exact: true }).check()
      await save.click()
      await expect(
        page.getByText(pt ? 'Preferência regional salva.' : 'Regional preference saved.', { exact: true }),
      ).toBeVisible()
      expect(state.writes).toEqual([{ mode: 'countries', countryCodes: ['BR'] }])
      await page.reload()
      await expect(field).toHaveValue('countries')
      await expect(page.getByRole('checkbox', { name: brazil, exact: true })).toBeChecked()
      await expect(page.getByRole('checkbox', { name: pt ? 'Inglês' : 'English', exact: true })).not.toBeChecked()
      await field.selectOption('unrestricted')
      await save.click()
      await expect.poll(() => state.mode).toBe('unrestricted')
      expect(state.countryCodes).toEqual([])
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze())
          .violations,
      ).toEqual([])
    })
    test('412 preserves draft, requires reload and a new explicit save', async ({ page }) => {
      const state = await setup(page, locale)
      const field = page.getByRole('combobox', { name: label, exact: true })
      const save = page.getByRole('button', { name: saveLabel, exact: true })
      await field.selectOption('countries')
      await page.getByRole('checkbox', { name: brazil, exact: true }).check()
      state.mode = 'unrestricted'
      state.version++
      await save.click()
      await expectSafeError(
        page,
        locale,
        pt
          ? 'A preferência regional mudou em outra aba. Seu rascunho foi preservado. Recarregue a versão atual antes de salvar novamente.'
          : 'Your regional preference changed in another tab. Your draft was preserved. Reload the current version before saving again.',
      )
      await expect(save).toBeDisabled()
      await page
        .getByRole('button', { name: pt ? 'Recarregar preferência regional' : 'Reload regional preference' })
        .click()
      await expect(save).toBeEnabled()
      await expect(field).toHaveValue('countries')
      await expect(page.getByRole('checkbox', { name: brazil, exact: true })).toBeChecked()
      expect(state.writes).toHaveLength(1)
      await save.click()
      await expect.poll(() => state.mode).toBe('countries')
      expect(state.writes).toHaveLength(2)
      await expect(page.locator('body')).not.toContainText('CANARY')
    })
    test('429 keeps the manual draft and never renders remote text', async ({ page }) => {
      const state = await setup(page, locale)
      await page.getByRole('combobox', { name: label, exact: true }).selectOption('unrestricted')
      state.failure = { status: 429, code: 'UNRECOGNIZED' }
      await page.getByRole('button', { name: saveLabel, exact: true }).click()
      await expectSafeError(
        page,
        locale,
        pt
          ? 'Muitas tentativas seguidas. Por segurança, espere 2 segundos antes de tentar de novo.'
          : 'Too many attempts in a row. For your security, wait 2 seconds before trying again.',
      )
      await expect(page.getByRole('combobox', { name: label, exact: true })).toHaveValue('unrestricted')
    })
  })
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`regional suggestion / ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    test('suggestion requires explicit save and a manual edit wins while it is pending', async ({ page }) => {
      const state = await setup(page, locale)
      await page.route('**/api/v1/me/region-detection/capability', (route) =>
        route.fulfill({ json: { enabled: true } }),
      )
      let finish!: () => void
      let started!: () => void
      const began = new Promise<void>((resolve) => {
        started = resolve
      })
      const pending = new Promise<void>((resolve) => {
        finish = resolve
      })
      await page.route('**/api/v1/me/region-detection', async (route) => {
        expect(route.request().method()).toBe('POST')
        expect(route.request().postData()).toBeNull()
        expect(route.request().headers()['x-xsrf-token']).toBe('synthetic')
        started()
        await pending
        await route.fulfill({ json: { status: 'detected', countryCode: 'BR', observedAt: '2026-10-21T12:00:00Z' } })
      })
      await page.reload()
      await page
        .getByRole('button', {
          name: pt ? 'Sugerir país pela conexão' : 'Suggest country from connection',
          exact: true,
        })
        .click()
      await began
      const field = page.getByRole('combobox', {
        name: pt ? 'Preferência regional' : 'Regional preference',
        exact: true,
      })
      await field.selectOption('unrestricted')
      finish()
      await expect(
        page.getByText(pt ? 'Sua escolha manual foi mantida.' : 'Your manual choice was kept.', { exact: true }),
      ).toBeVisible()
      await expect(field).toHaveValue('unrestricted')
      expect(state.writes).toEqual([])
      await page
        .getByRole('button', { name: pt ? 'Salvar preferência regional' : 'Save regional preference', exact: true })
        .click()
      await expect.poll(() => state.mode).toBe('unrestricted')
      await expect(page.locator('html')).toHaveAttribute('lang', locale)
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze())
          .violations,
      ).toEqual([])
    })
    test('saving one resource and failing the other never reports joint success', async ({ page }) => {
      const state = await setup(page, locale)
      const profile = {
        displayName: 'Ana',
        bio: null,
        countryCode: null as string | null,
        subdivisionCode: null,
        regionCode: null,
        version: 0,
      }
      await page.route('**/api/v1/me/profile', async (route) => {
        if (route.request().method() === 'PATCH') {
          expect(route.request().headers()['if-match']).toBe(`"${String(profile.version)}"`)
          Object.assign(profile, route.request().postDataJSON() as object, { version: profile.version + 1 })
        }
        await route.fulfill({ json: { ...profile, version: String(profile.version) } })
      })
      await expect(
        page.getByRole('combobox', { name: pt ? 'Preferência regional' : 'Regional preference', exact: true }),
      ).toBeVisible()
      await page.goto('/account/profile')
      await page.getByRole('combobox', { name: pt ? 'País' : 'Country', exact: true }).selectOption('BR')
      await page.getByRole('button', { name: pt ? 'Salvar perfil' : 'Save profile', exact: true }).click()
      await expect(page.getByText(pt ? 'Perfil salvo.' : 'Profile saved.', { exact: true })).toBeVisible()
      state.failure = { status: 412, code: 'PRECONDITION_FAILED' }
      await page.goto('/account/preferences')
      await page
        .getByRole('combobox', { name: pt ? 'Preferência regional' : 'Regional preference', exact: true })
        .selectOption('unrestricted')
      await page
        .getByRole('button', { name: pt ? 'Salvar preferência regional' : 'Save regional preference', exact: true })
        .click()
      await expect(
        page.getByRole('button', {
          name: pt ? 'Recarregar preferência regional' : 'Reload regional preference',
          exact: true,
        }),
      ).toBeVisible()
      expect(profile.countryCode).toBe('BR')
      expect(state.mode).toBe('pending')
      await expect(
        page.getByText(pt ? 'Preferência regional salva.' : 'Regional preference saved.', { exact: true }),
      ).toHaveCount(0)
      await expect(page.getByText(pt ? 'Perfil salvo.' : 'Profile saved.', { exact: true })).toHaveCount(0)
    })
  })
}
