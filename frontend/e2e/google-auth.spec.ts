import { expect, test } from '@playwright/test'
import { withSessionContext } from './support/private-context.ts'

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`Google browser continuation ${locale}`, () => {
    test.use({ locale })
    test('navigates through the internal challenge and needs a new gesture to issue the Duora session', async ({
      page,
    }) => {
      const pt = locale === 'pt-BR'
      let authorized = 0,
        completed = 0
      await page.route('**/api/v1/**', async (route) => {
        const request = route.request()
        const path = new URL(request.url()).pathname
        switch (path) {
          case '/api/v1/auth/google/options':
            await route.fulfill({ json: { enabled: true, registrationEnabled: false, linkingEnabled: false } })
            return
          case '/api/v1/auth/csrf':
            await route.fulfill({ status: 204, headers: { 'Set-Cookie': 'XSRF-TOKEN=synthetic-google-csrf; Path=/' } })
            return
          case '/api/v1/auth/google/requests':
            expect(request.postDataJSON()).toEqual({ purpose: 'Login' })
            expect(request.headers()['x-xsrf-token']).toBe('synthetic-google-csrf')
            await route.fulfill({ json: { authorizationUrl: '/api/v1/auth/google/authorize' } })
            return
          case '/api/v1/auth/google/authorize':
            authorized++
            expect(request.isNavigationRequest()).toBe(true)
            // WebKit cannot fulfill an intercepted 302; this synthetic page performs the final navigation.
            await route.fulfill({
              contentType: 'text/html',
              body: '<!doctype html><meta http-equiv="refresh" content="0;url=/google/continue">',
            })
            return
          case '/api/v1/auth/google/flow':
            await route.fulfill({ json: { nextStep: 'Login', expiresAt: new Date(Date.now() + 300000).toISOString() } })
            return
          case '/api/v1/auth/google/completions':
            expect(request.postDataJSON()).toEqual({})
            expect(request.headers()['x-xsrf-token']).toBe('synthetic-google-csrf')
            completed++
            await route.fulfill({ status: 204 })
            return
          case '/api/v1/me':
            await route.fulfill(
              completed
                ? {
                    json: withSessionContext({
                      id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
                      email: 'synthetic-google@example.test',
                      status: 'Active',
                      emailConfirmed: true,
                      currentTermsAccepted: true,
                      mfaEnabled: false,
                      roles: ['participant'],
                    }),
                  }
                : { status: 401 },
            )
            return
          case '/api/v1/me/sessions':
          case '/api/v1/me/external-logins':
            await route.fulfill({ json: { items: [] } })
            return
          default:
            throw new Error(`Unexpected Google browser request: ${request.method()} ${path}`)
        }
      })
      await page.goto('/login')
      await expect(page.getByLabel(pt ? 'Senha' : 'Password', { exact: true })).toBeVisible()
      await page
        .getByRole('button', { name: pt ? 'Continuar com Google' : 'Continue with Google', exact: true })
        .click()
      await expect(page).toHaveURL(/\/google\/continue$/)
      const completion = page.getByRole('button', { name: pt ? 'Entrar no Duora' : 'Sign in to Duora', exact: true })
      await expect(completion).toBeVisible()
      expect(authorized).toBe(1)
      expect(completed).toBe(0)
      await completion.click()
      await expect(page).toHaveURL(/\/account$/)
      await expect(page.getByRole('heading', { name: pt ? 'Sua conta' : 'Your account', exact: true })).toBeVisible()
      expect(completed).toBe(1)
    })
  })
}
