import { expect, test } from '@playwright/test'

for (const locale of ['pt-BR', 'en-US']) {
  test.describe(locale, () => {
    test.use({ locale, timezoneId: 'America/New_York' })
    test('event zone and browser zone survive the API boundary', async ({ page, browserName }) => {
      const ny = browserName === 'chromium' ? 'Horário New York' : 'Horário Nova York'
      const sp = browserName === 'chromium' ? 'Sao Paulo Time' : 'São Paulo Time'
      await page.route('**/api/v1/me', (route) =>
        route.fulfill({
          json: {
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'synthetic@example.com',
            status: 'Active',
            emailConfirmed: true,
            mfaEnabled: false,
            roles: ['participant'],
          },
        }),
      )
      await page.route('**/api/v1/events', (route) =>
        route.fulfill({
          json: [
            {
              eventId: 'one',
              title: 'São Paulo',
              startsAt: '2026-10-16T23:00:00Z',
              timeZone: 'America/Sao_Paulo',
              checkInOpensAt: '2026-10-16T22:50:00Z',
              state: 'Published',
            },
            {
              eventId: 'two',
              title: 'New York',
              startsAt: '2026-11-01T06:30:00Z',
              timeZone: 'America/New_York',
              checkInOpensAt: '2026-11-01T06:20:00Z',
              state: 'Published',
            },
            {
              eventId: 'three',
              title: 'Legacy',
              startsAt: '2026-10-16T23:00:00Z',
              timeZone: null,
              checkInOpensAt: '2026-10-16T22:50:00Z',
              state: 'Published',
            },
          ],
        }),
      )
      await page.route('**/api/v1/events/*/participation', (route) =>
        route.fulfill({ json: { state: 'Unregistered', roomId: null } }),
      )
      await page.goto('/events')
      const times = page.locator('time')
      await expect(times).toHaveText(
        locale === 'pt-BR'
          ? [
              `Horário do evento: 16/10/2026, 20:00 — BRT · UTC−03:00\nNo fuso do navegador: 16/10/2026, 19:00 — ${ny} · UTC−04:00`,
              `Horário do evento: 01/11/2026, 01:30 — ${ny} · UTC−05:00`,
              `Referência: 16/10/2026, 23:00 UTC; fuso do evento não informado\nNo fuso do navegador: 16/10/2026, 19:00 — ${ny} · UTC−04:00`,
            ]
          : [
              `Event time: 10/16/26, 8:00 PM — ${sp} · UTC−03:00\nIn your browser’s time zone: 10/16/26, 7:00 PM — ET · UTC−04:00`,
              'Event time: 11/1/26, 1:30 AM — ET · UTC−05:00',
              'Reference: 10/16/26, 11:00 PM UTC; event time zone not provided\nIn your browser’s time zone: 10/16/26, 7:00 PM — ET · UTC−04:00',
            ],
      )
      await expect(times.first()).toHaveAttribute('datetime', '2026-10-16T23:00:00Z')
    })
  })
}
