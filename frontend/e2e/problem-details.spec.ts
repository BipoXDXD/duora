import { withSessionContext, passwordChangeBootstrap } from './support/private-context.ts'
import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

const CANARY = '<img src=x onerror="window.CANARY=1">CANARY SQL password unexpected text'
const PASSWORD = 'a long synthetic passphrase here'

async function fail(page: Page, endpoint: string, problem: readonly [number, string]): Promise<void> {
  const [status, code] = problem
  await page.route(`**/api/v1${endpoint}`, (route) =>
    route.fulfill({
      status,
      contentType: 'application/problem+json',
      headers: { 'Retry-After': '2' },
      body: JSON.stringify({
        type: 'urn:duora:problem:canary',
        status,
        code,
        title: CANARY,
        detail: CANARY,
        errors: {
          email: [CANARY],
          newPassword: [CANARY],
          displayName: [CANARY],
          preferences: [CANARY],
          locale: [CANARY],
          timeZone: [CANARY],
        },
        traceId: 'synthetic',
      }),
    }),
  )
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(locale, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    const generic = pt ? 'Algo deu errado do nosso lado.' : 'Something went wrong on our side.'
    async function safeAlert(page: Page, message = generic): Promise<void> {
      await expect(page.getByRole('alert').first()).toContainText(message)
      await expect(page.locator('body')).not.toContainText('CANARY')
      expect(await page.locator('body').innerHTML()).not.toContain('CANARY')
      expect(await page.evaluate(() => Object.hasOwn(window, 'CANARY'))).toBe(false)
      await expect(page.locator('html')).toHaveAttribute('lang', locale)
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze())
          .violations,
      ).toEqual([])
    }
    async function login(page: Page): Promise<void> {
      await page.goto('/login')
      await page.getByLabel(pt ? 'E-mail' : 'Email', { exact: true }).fill('canary@example.com')
      await page.getByLabel(pt ? 'Senha' : 'Password', { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: pt ? 'Entrar' : 'Sign in', exact: true }).click()
    }

    for (const [status, code] of [
      [401, 'INVALID_CREDENTIALS'],
      [400, 'VALIDATION_FAILED'],
      [503, 'TEMPORARILY_UNAVAILABLE'],
      [400, 'UNKNOWN'],
      [400, 'constructor'],
      [400, '__proto__'],
      [400, 'toString'],
    ] as const) {
      test(`login ${code} ignores server text`, async ({ page }) => {
        await FakeDuoraApi.install(page)
        await fail(page, '/auth/login', [status, code])
        await login(page)
        await safeAlert(
          page,
          code === 'INVALID_CREDENTIALS'
            ? pt
              ? 'E-mail ou senha incorretos.'
              : 'Incorrect email or password.'
            : generic,
        )
      })
    }

    test('registration and recovery validation never echo field messages', async ({ page }) => {
      await FakeDuoraApi.install(page)
      await fail(page, '/auth/register', [400, 'VALIDATION_FAILED'])
      await page.goto('/register')
      await page.getByLabel(pt ? 'E-mail' : 'Email', { exact: true }).fill('canary@example.com')
      await page.getByLabel(pt ? 'Senha' : 'Password', { exact: true }).fill(PASSWORD)
      for (const checkbox of await page.getByRole('checkbox').all()) await checkbox.check()
      await page.getByRole('button', { name: pt ? 'Criar conta' : 'Create account', exact: true }).click()
      await safeAlert(page)
      await fail(page, '/auth/password-reset-requests', [503, 'TEMPORARILY_UNAVAILABLE'])
      await page.goto('/forgot-password')
      await page.getByLabel(pt ? 'E-mail' : 'Email', { exact: true }).fill('canary@example.com')
      await page.getByRole('button', { name: pt ? 'Enviar link' : 'Send link', exact: true }).click()
      await safeAlert(page)
    })

    for (const [path, endpoint] of [
      ['/account', '/me/sessions'],
      ['/account/profile', '/me/profile'],
      ['/account/preferences', '/me/preferences'],
      ['/events', '/events'],
      ['/rooms/canary', '/rooms/canary'],
      ['/account/selfie', '/me/selfie-verifications/current'],
    ] as const) {
      test(`${path} read failure uses localized fallback`, async ({ page }) => {
        const api = await FakeDuoraApi.install(page)
        api.seedConfirmedUser('canary@example.com', PASSWORD)
        await login(page)
        await expect(page).toHaveURL(/\/account$/)
        await expect(page.getByText('canary@example.com', { exact: true })).toBeVisible()
        await fail(page, endpoint, [503, 'TEMPORARILY_UNAVAILABLE'])
        await page.goto(path)
        await safeAlert(page)
      })
    }

    test('profile save and sign out never display mutation error text', async ({ page }) => {
      const api = await FakeDuoraApi.install(page)
      api.seedConfirmedUser('canary@example.com', PASSWORD)
      await login(page)
      await expect(page).toHaveURL(/\/account$/)
      await expect(page.getByText('canary@example.com', { exact: true })).toBeVisible()
      await page.goto('/account/profile')
      await page.getByLabel(pt ? 'Nome de exibição' : 'Display name', { exact: true }).fill('Synthetic User')
      await fail(page, '/me/profile', [400, 'VALIDATION_FAILED'])
      await page.getByRole('button', { name: pt ? 'Salvar perfil' : 'Save profile', exact: true }).click()
      await safeAlert(
        page,
        pt ? 'Confira os campos do perfil e tente de novo.' : 'Check the profile fields and try again.',
      )
      await page.goto('/account')
      await fail(page, '/auth/logout', [503, 'TEMPORARILY_UNAVAILABLE'])
      await page.getByRole('button', { name: pt ? 'Sair' : 'Sign out', exact: true }).click()
      await safeAlert(page)
    })

    test('confirmation and reset translate invalid token safely', async ({ page }) => {
      await FakeDuoraApi.install(page)
      const fragment = '#userId=11111111-1111-4111-8111-111111111111&token=synthetic-token'
      await fail(page, '/auth/confirm-email', [400, 'INVALID_TOKEN'])
      await page.goto('/confirm-email' + fragment)
      await page.getByLabel(pt ? 'Senha' : 'Password', { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: pt ? 'Confirmar e-mail' : 'Confirm email', exact: true }).click()
      await safeAlert(page, pt ? 'Link inválido ou senha incorreta.' : 'Invalid link or incorrect password.')
      await fail(page, '/auth/password-resets', [400, 'INVALID_TOKEN'])
      await page.goto('/reset-password' + fragment)
      await page.getByLabel(pt ? 'Senha nova' : 'New password', { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: pt ? 'Salvar senha nova' : 'Save new password', exact: true }).click()
      await safeAlert(
        page,
        pt
          ? 'Este link de recuperação é inválido, expirou ou já foi usado.'
          : 'This reset link is invalid, has expired or was already used.',
      )
    })

    test('MFA enrollment and restricted password form ignore server text', async ({ page }) => {
      await FakeDuoraApi.install(page)
      await page.route('**/api/v1/me', (route) =>
        route.fulfill({
          json: withSessionContext({
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'synthetic@example.test',
            status: 'Active',
            emailConfirmed: true,
            mfaEnabled: false,
            roles: ['operator'],
          }),
        }),
      )
      await fail(page, '/me/mfa/enrollments', [503, 'TEMPORARILY_UNAVAILABLE'])
      await page.goto('/account/enable-mfa')
      await page
        .getByRole('button', { name: pt ? 'Configurar autenticador' : 'Set up authenticator', exact: true })
        .click()
      await safeAlert(page)
      await page.unroute('**/api/v1/me')
      await page.route('**/api/v1/me', (route) =>
        route.fulfill({
          status: 403,
          contentType: 'application/problem+json',
          json: { ...passwordChangeBootstrap('0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'), title: CANARY, detail: CANARY },
        }),
      )
      await fail(page, '/me/password-changes', [400, 'VALIDATION_FAILED'])
      await page.goto('/change-initial-password')
      await page.getByLabel(pt ? 'Senha temporária' : 'Temporary password', { exact: true }).fill(PASSWORD)
      await page.getByLabel(pt ? 'Senha nova' : 'New password', { exact: true }).fill(PASSWORD)
      await page.getByLabel(pt ? 'Confirme a nova senha' : 'Confirm new password', { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: pt ? 'Salvar senha nova' : 'Save new password', exact: true }).click()
      await safeAlert(page)
    })

    test('unsupported or untyped bootstrap403 remains blocked without provisioning', async ({ page }) => {
      await FakeDuoraApi.install(page)
      for (const code of ['MFA_ENROLLMENT_REQUIRED', 'PASSWORD_CHANGE_REQUIRED']) {
        await fail(page, '/me', [403, code])
        await page.goto(code === 'MFA_ENROLLMENT_REQUIRED' ? '/account/enable-mfa' : '/change-initial-password')
        await safeAlert(page)
        await expect(
          page.getByRole('button', { name: pt ? 'Configurar autenticador' : 'Set up authenticator', exact: true }),
        ).toHaveCount(0)
        await expect(page.getByLabel(pt ? 'Senha temporária' : 'Temporary password', { exact: true })).toHaveCount(0)
      }
    })

    test('session bootstrap failure uses localized fallback', async ({ page }) => {
      await FakeDuoraApi.install(page)
      await fail(page, '/me', [503, 'TEMPORARILY_UNAVAILABLE'])
      await page.goto('/account')
      await safeAlert(page)
    })
  })
}
