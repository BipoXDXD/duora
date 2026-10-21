import { expect, test, type Page } from '@playwright/test'
import { authenticatedPage, editableResource, expectSafeError, type Locale } from './support/problem-canary.ts'

async function settings(page: Page, locale: Locale) {
  await authenticatedPage(page, locale)
  const preferences = await editableResource(page, '/me/preferences', {
    communicationLanguages: ['en'],
    allowDifferentDisplayLocales: false,
    declarationRequired: false,
    version: '1',
  })
  const display = await editableResource(page, '/me/locale', { locale, version: '1' })
  const zone = await editableResource(page, '/me/time-zone', { timeZone: 'UTC', version: '1' })
  await page.route('**/api/v1/me/time-zone/options', (route) =>
    route.fulfill({ json: { timeZones: ['UTC', 'America/New_York', 'America/Sao_Paulo'] } }),
  )
  await page.goto('/account/preferences')
  return { preferences, display, zone }
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`settings canaries ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    const labels = {
      savePreferences: pt ? 'Salvar preferências' : 'Save preferences',
      saveLocale: pt ? 'Salvar idioma de exibição' : 'Save display language',
      saveZone: pt ? 'Salvar fuso horário' : 'Save time zone',
      review: pt ? 'Revisar versão atual' : 'Review current version',
      zone: pt ? 'Fuso horário da conta (opcional)' : 'Account time zone (optional)',
      locale: pt ? 'Idioma da interface e conteúdo' : 'Interface and content language',
      portuguese: pt ? 'Português' : 'Portuguese',
    }

    for (const target of ['preferences', 'display', 'zone'] as const) {
      for (const failure of [
        { status: 400, code: 'VALIDATION_FAILED' },
        { status: 428, code: 'PRECONDITION_REQUIRED' },
        { status: 503, code: 'TEMPORARILY_UNAVAILABLE' },
      ]) {
        test(`${target} ${failure.code} retains draft without echoing server text`, async ({ page }) => {
          const resources = await settings(page, locale)
          resources[target].failure = failure
          if (target === 'preferences')
            await page.getByRole('checkbox', { name: labels.portuguese, exact: true }).check()
          if (target === 'display')
            await page.getByLabel(labels.locale, { exact: true }).selectOption(pt ? 'en-US' : 'pt-BR')
          if (target === 'zone') await page.getByLabel(labels.zone, { exact: true }).fill('America/Sao_Paulo')
          const name =
            target === 'preferences'
              ? labels.savePreferences
              : target === 'display'
                ? labels.saveLocale
                : labels.saveZone
          await page.getByRole('button', { name, exact: true }).click()
          await expectSafeError(page, locale)
          if (target === 'preferences')
            await expect(page.getByRole('checkbox', { name: labels.portuguese, exact: true })).toBeChecked()
          if (target === 'display')
            await expect(page.getByLabel(labels.locale, { exact: true })).toHaveValue(pt ? 'en-US' : 'pt-BR')
          if (target === 'zone')
            await expect(page.getByLabel(labels.zone, { exact: true })).toHaveValue('America/Sao_Paulo')
        })
      }
    }

    for (const code of ['UNKNOWN', 'constructor', '__proto__', 'toString']) {
      test(`locale mutation ${code} keeps the current UI language and uses fallback`, async ({ page }) => {
        const { display } = await settings(page, locale)
        display.failure = { status: 400, code }
        await page.getByLabel(labels.locale, { exact: true }).selectOption(pt ? 'en-US' : 'pt-BR')
        await page.getByRole('button', { name: labels.saveLocale, exact: true }).click()
        await expectSafeError(page, locale)
        // Failure must not change the persisted account locale or the language after navigation.
        await page.reload()
        await expect(page.getByLabel(labels.locale, { exact: true })).toHaveValue(locale)
        await expect(page.locator('html')).toHaveAttribute('lang', locale)
      })
    }

    test('preferences 412 reload preserves draft and resubmits the current version', async ({ page }) => {
      const { preferences } = await settings(page, locale)
      const portuguese = page.getByRole('checkbox', { name: labels.portuguese, exact: true })
      const save = page.getByRole('button', { name: labels.savePreferences, exact: true })
      await portuguese.check()
      preferences.failure = { status: 412, code: 'PRECONDITION_FAILED' }
      await save.click()
      await expectSafeError(
        page,
        locale,
        pt
          ? 'Outra aba alterou estas preferências. Revise a versão atual antes de salvar seu rascunho.'
          : 'Another tab changed these preferences. Review the current version before saving your draft.',
      )
      await expect(save).toBeDisabled()
      preferences.value = { ...preferences.value, version: '2' }
      await page.getByRole('button', { name: labels.review, exact: true }).click()
      await expect(page.getByRole('status')).toContainText(
        pt
          ? 'Versão atual carregada. Seu rascunho foi preservado; compare antes de salvar.'
          : 'Current version loaded. Your draft is preserved; compare before saving.',
      )
      await expect(portuguese).toBeChecked()
      preferences.failure = null
      const request = page.waitForRequest((r) => r.url().endsWith('/me/preferences') && r.method() === 'PUT')
      await save.click()
      const sent = await request
      expect(sent.headers()['if-match']).toBe('"2"')
      expect(sent.postDataJSON()).toEqual({ communicationLanguages: ['en', 'pt'], allowDifferentDisplayLocales: false })
      await expect(page.getByRole('status')).toContainText(pt ? 'Preferências salvas.' : 'Preferences saved.')
    })

    test('time zone 412 reload preserves draft and resubmits the current version', async ({ page }) => {
      const { zone } = await settings(page, locale)
      const field = page.getByLabel(labels.zone, { exact: true })
      const save = page.getByRole('button', { name: labels.saveZone, exact: true })
      await field.fill('America/Sao_Paulo')
      zone.failure = { status: 412, code: 'PRECONDITION_FAILED' }
      await save.click()
      await expectSafeError(
        page,
        locale,
        pt
          ? 'Outra aba alterou seu fuso. Recarregue a versão atual; sua escolha será preservada para revisão.'
          : 'Another tab changed your time zone. Reload the current version; your choice will be kept for review.',
      )
      await expect(save).toBeDisabled()
      zone.value = { timeZone: 'America/New_York', version: '2' }
      await page
        .getByRole('button', { name: pt ? 'Recarregar fuso atual' : 'Reload current time zone', exact: true })
        .click()
      await expect(page.getByRole('status')).toContainText('America/New_York')
      await expect(field).toHaveValue('America/Sao_Paulo')
      zone.failure = null
      const request = page.waitForRequest((r) => r.url().endsWith('/me/time-zone') && r.method() === 'PUT')
      await save.click()
      const sent = await request
      expect(sent.headers()['if-match']).toBe('"2"')
      expect(sent.postDataJSON()).toEqual({ timeZone: 'America/Sao_Paulo' })
      await expect(page.getByRole('status')).toContainText(pt ? 'Fuso horário salvo.' : 'Time zone saved.')
    })

    test('locale 412 review restores the saved choice without switching the UI on failure', async ({ page }) => {
      const { display } = await settings(page, locale)
      const field = page.getByLabel(labels.locale, { exact: true })
      const save = page.getByRole('button', { name: labels.saveLocale, exact: true })
      const other = pt ? 'en-US' : 'pt-BR'
      await field.selectOption(other)
      display.failure = { status: 412, code: 'PRECONDITION_FAILED' }
      await save.click()
      await expectSafeError(page, locale)
      await expect(field).toHaveValue(other)
      display.value = { locale, version: '2' }
      await page.getByRole('button', { name: labels.review, exact: true }).click()
      await expect(field).toHaveValue(locale)
      await expect(page.getByRole('alert')).toHaveCount(0)
      await expect(page.locator('html')).toHaveAttribute('lang', locale)
      await field.selectOption(other)
      display.failure = null
      display.value = { locale: other, version: '3' }
      const request = page.waitForRequest((r) => r.url().endsWith('/me/locale') && r.method() === 'PUT')
      await save.click()
      const sent = await request
      expect(sent.headers()['if-match']).toBe('"2"')
      expect(sent.postDataJSON()).toEqual({ locale: other })
      await expect(page.locator('html')).toHaveAttribute('lang', other)
      await expect(page.getByRole('status')).toContainText(pt ? 'Display language saved.' : 'Idioma de exibição salvo.')
    })

    test('429 shows localized Retry-After without an automatic mutation retry', async ({ page }) => {
      await page.clock.install()
      const { preferences } = await settings(page, locale)
      preferences.failure = { status: 429, code: 'RATE_LIMITED' }
      const writes: string[] = []
      page.on('request', (request) => {
        if (request.method() === 'PUT') writes.push(request.url())
      })
      await page.getByRole('button', { name: labels.savePreferences, exact: true }).click()
      await expectSafeError(
        page,
        locale,
        pt
          ? 'Muitas tentativas seguidas. Por segurança, espere 2 segundos antes de tentar de novo.'
          : 'Too many attempts in a row. For your security, wait 2 seconds before trying again.',
      )
      await page.clock.runFor('00:02:00')
      // Flush requests started by timer callbacks, including asynchronous CSRF refresh.
      await page.evaluate(async () => {
        await fetch('/api/v1/me')
      })
      await page.waitForLoadState('networkidle')
      expect(writes).toHaveLength(1)
    })
  })
}
