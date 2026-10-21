import { AxeBuilder } from '@axe-core/playwright'
import { expect, type Page, type Route } from '@playwright/test'

export type Locale = 'pt-BR' | 'en-US'
export const CANARY = '<img src=x onerror="window.CANARY=1">CANARY SQL password unexpected text'
export const GENERIC = {
  'pt-BR': 'Algo deu errado do nosso lado. Tente de novo em instantes.',
  'en-US': 'Something went wrong on our side. Please try again in a moment.',
} as const
export interface Failure {
  readonly status: number
  readonly code: string
}

export async function fulfillProblem(route: Route, failure: Failure): Promise<void> {
  await route.fulfill({
    status: failure.status,
    contentType: 'application/problem+json',
    headers: { 'Retry-After': '2' },
    body: JSON.stringify({
      status: failure.status,
      code: failure.code,
      type: 'urn:duora:problem:canary',
      title: CANARY,
      detail: CANARY,
      errors: { preferences: [CANARY], locale: [CANARY], timeZone: [CANARY], code: [CANARY] },
      traceId: 'synthetic-canary',
    }),
  })
}

export async function expectSafeError(page: Page, locale: Locale, message: string = GENERIC[locale]): Promise<void> {
  await expect(page.getByRole('alert')).toContainText(message)
  await expect(page.locator('html')).toHaveAttribute('lang', locale)
  await expect(page.locator('body')).not.toContainText('CANARY')
  expect(await page.locator('body').innerHTML()).not.toContain('CANARY')
  expect(await page.evaluate(() => Object.hasOwn(window, 'CANARY'))).toBe(false)
  expect(
    (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze())
      .violations,
  ).toEqual([])
}

/** Intercepts the HTTP boundary; UI, parser, mutations and translations remain real. */
export async function authenticatedPage(page: Page, locale: Locale): Promise<void> {
  await page.route('**/api/v1/me', (route) =>
    route.fulfill({
      json: {
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
        email: 'canary@example.com',
        status: 'Active',
        emailConfirmed: true,
        mfaEnabled: false,
        roles: ['participant'],
        preferredLocale: locale,
        preferredTimeZone: null,
      },
    }),
  )
  await page.route('**/api/v1/auth/csrf', (route) =>
    route.fulfill({ status: 204, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic; Path=/; SameSite=Lax' } }),
  )
}

export async function editableResource<T extends object>(
  page: Page,
  path: string,
  value: T,
): Promise<{ value: T; failure: Failure | null }> {
  const resource = { value, failure: null as Failure | null }
  await page.route(`**/api/v1${path}`, async (route) => {
    if (route.request().method() === 'PUT' && resource.failure !== null) {
      await fulfillProblem(route, resource.failure)
    } else {
      await route.fulfill({ json: resource.value })
    }
  })
  return resource
}

export async function rejectPost(page: Page, path: string, failure: Failure): Promise<void> {
  await page.route(`**/api/v1${path}`, async (route) => {
    expect(route.request().method()).toBe('POST')
    await fulfillProblem(route, failure)
  })
}
