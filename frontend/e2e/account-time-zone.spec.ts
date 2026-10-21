import { expect, test } from '@playwright/test'

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(locale, () => {
    test.use({ locale, timezoneId: 'America/New_York' })
    test('the device action only fills; saving and removal survive a reload', async ({ page }) => {
      const stored = { timeZone: null as string | null, version: 0 }
      let writes = 0
      await page.route('**/api/v1/me', (route) =>
        route.fulfill({
          json: {
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'synthetic@example.com',
            status: 'Active',
            emailConfirmed: true,
            mfaEnabled: false,
            roles: ['participant'],
            preferredLocale: locale,
            preferredTimeZone: stored.timeZone,
          },
        }),
      )
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
      await page.route('**/api/v1/auth/csrf', (route) =>
        route.fulfill({ status: 204, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Lax' } }),
      )
      await page.route('**/api/v1/me/time-zone/options', (route) =>
        route.fulfill({ json: { timeZones: ['UTC', 'America/New_York', 'America/Sao_Paulo'] } }),
      )
      await page.route('**/api/v1/me/time-zone', async (route) => {
        if (route.request().method() === 'PUT') {
          expect(route.request().headers()['if-match']).toBe(`"${String(stored.version)}"`)
          expect(route.request().headers()['x-xsrf-token']).toBe('synthetic')
          stored.timeZone = (route.request().postDataJSON() as { timeZone: string | null }).timeZone
          stored.version++
          writes++
        }
        await route.fulfill({ json: { ...stored, version: String(stored.version) } })
      })
      await page.route('**/api/v1/me/regional-preferences', (route) =>
        route.fulfill({ json: { mode: 'pending', countryCodes: [], version: '0' } }),
      )
      await page.goto('/account/preferences')
      const field = page.getByRole('combobox', {
        name: locale === 'pt-BR' ? 'Fuso horário da conta (opcional)' : 'Account time zone (optional)',
      })
      const save = page.getByRole('button', {
        name: locale === 'pt-BR' ? 'Salvar fuso horário' : 'Save time zone',
        exact: true,
      })
      await expect(field).toHaveValue('')
      await page
        .getByRole('button', {
          name: locale === 'pt-BR' ? 'Usar fuso deste dispositivo' : 'Use this device’s time zone',
        })
        .click()
      await expect(field).toHaveValue('America/New_York')
      expect(writes).toBe(0)
      await field.fill('America/Sao_Paulo')
      await save.click()
      await expect(
        page.getByText(locale === 'pt-BR' ? 'Fuso horário salvo.' : 'Time zone saved.', { exact: true }),
      ).toBeVisible()
      expect(writes).toBe(1)
      await page.reload()
      await expect(field).toHaveValue('America/Sao_Paulo')
      await field.fill('')
      await save.click()
      await expect(
        page.getByText(locale === 'pt-BR' ? 'Fuso horário salvo.' : 'Time zone saved.', { exact: true }),
      ).toBeVisible()
      expect(writes).toBe(2)
      await page.reload()
      await expect(field).toHaveValue('')
    })
    test('an explicit saved choice overrides the device and disambiguates both repeated hours', async ({
      page,
      browserName,
    }) => {
      await page.route('**/api/v1/me', (route) =>
        route.fulfill({
          json: {
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'synthetic@example.com',
            status: 'Active',
            emailConfirmed: true,
            mfaEnabled: false,
            roles: ['participant'],
            preferredTimeZone: 'America/Sao_Paulo',
          },
        }),
      )
      await page.route('**/api/v1/events', (route) =>
        route.fulfill({
          json: [
            {
              eventId: 'one',
              title: 'First occurrence',
              startsAt: '2026-11-01T05:30:00Z',
              timeZone: 'America/New_York',
              checkInOpensAt: '2026-11-01T05:20:00Z',
              state: 'Published',
            },
            {
              eventId: 'two',
              title: 'Second occurrence',
              startsAt: '2026-11-01T06:30:00Z',
              timeZone: 'America/New_York',
              checkInOpensAt: '2026-11-01T06:20:00Z',
              state: 'Published',
            },
          ],
        }),
      )
      await page.route('**/api/v1/events/*/participation', (route) =>
        route.fulfill({ json: { state: 'Unregistered', roomId: null } }),
      )
      await page.goto('/events')
      const ny = browserName === 'chromium' ? 'Horário New York' : 'Horário Nova York'
      // WebKit uses platform ICU: macOS retains the accent; Linux and Chromium do not.
      const sp = browserName === 'webkit' && process.platform === 'darwin' ? 'São Paulo Time' : 'Sao Paulo Time'
      await expect(page.locator('time')).toHaveText(
        locale === 'pt-BR'
          ? [
              `Horário do evento: 01/11/2026, 01:30 — ${ny} · UTC−04:00\nNo seu fuso salvo: 01/11/2026, 02:30 — BRT · UTC−03:00`,
              `Horário do evento: 01/11/2026, 01:30 — ${ny} · UTC−05:00\nNo seu fuso salvo: 01/11/2026, 03:30 — BRT · UTC−03:00`,
            ]
          : [
              `Event time: 11/1/26, 1:30 AM — ET · UTC−04:00\nIn your saved time zone: 11/1/26, 2:30 AM — ${sp} · UTC−03:00`,
              `Event time: 11/1/26, 1:30 AM — ET · UTC−05:00\nIn your saved time zone: 11/1/26, 3:30 AM — ${sp} · UTC−03:00`,
            ],
      )
      await expect(page.locator('time').first()).toHaveAttribute('datetime', '2026-11-01T05:30:00Z')
      await expect(page.locator('time').last()).toHaveAttribute('datetime', '2026-11-01T06:30:00Z')
    })
  })
}
