import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { withSessionContext, SYNTHETIC_SESSION_ID, SYNTHETIC_RENEWED_SESSION_ID } from './support/private-context.ts'

async function accountApi(page: Page, roles = ['participant'], initialMfa = false) {
  const state = { enabled: initialMfa, sid: SYNTHETIC_SESSION_ID, disabled: 0 }
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request(),
      path = new URL(request.url()).pathname
    if (path === '/api/v1/me') {
      await route.fulfill({
        json: withSessionContext(
          {
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'optional-mfa@example.test',
            status: 'Active',
            emailConfirmed: true,
            currentTermsAccepted: true,
            mfaEnabled: state.enabled,
            roles,
          },
          state.sid,
        ),
      })
      return
    }
    if (path === '/api/v1/me/sessions') {
      await route.fulfill({ json: { items: [] } })
      return
    }
    if (path.startsWith('/api/v1/me/mfa')) expect(request.headers()['x-xsrf-token']).toBe('synthetic')
    if (path === '/api/v1/me/mfa/enrollments') {
      await route.fulfill({
        json: {
          sharedKey: 'SYNTHETICKEY',
          authenticatorUri: 'otpauth://totp/Duora:optional-mfa%40example.test?secret=SYNTHETICKEY&issuer=Duora',
        },
      })
      return
    }
    if (path === '/api/v1/me/mfa/confirmations') {
      expect(request.postDataJSON()).toEqual({ code: '123456' })
      state.enabled = true
      state.sid = SYNTHETIC_RENEWED_SESSION_ID
      await route.fulfill({ json: { recoveryCodes: ['synthetic-optional-recovery'], resultSessionId: state.sid } })
      return
    }
    if (path === '/api/v1/me/mfa' && request.method() === 'DELETE') {
      expect(request.postData()).toBeNull()
      expect(roles).toEqual(['participant'])
      state.disabled++
      state.enabled = false
      state.sid = '0199f1a2-3b4c-7d5e-8f60-000000000101'
      await route.fulfill({ status: 204 })
      return
    }
    await route.fulfill({ status: 404 })
  })
  return state
}

const COPY = [
  {
    locale: 'pt-BR',
    enable: 'Ativar 2FA',
    disable: 'Desativar 2FA',
    start: 'Configurar autenticador',
    code: 'Código do autenticador',
    activate: 'Ativar MFA',
    saved: 'Guardei os códigos, continuar',
    cancel: 'Manter 2FA',
    confirm: 'Confirmar desativação',
    group: 'Desativar a autenticação em duas etapas?',
    done: 'Autenticação em duas etapas desativada.',
    required: 'A autenticação em duas etapas é obrigatória para sua conta.',
    back: 'Voltar para a conta',
  },
  {
    locale: 'en-US',
    enable: 'Turn on 2FA',
    disable: 'Turn off 2FA',
    start: 'Set up authenticator',
    code: 'Authenticator code',
    activate: 'Enable MFA',
    saved: 'I saved the codes, continue',
    cancel: 'Keep 2FA',
    confirm: 'Confirm turning off 2FA',
    group: 'Turn off two-factor authentication?',
    done: 'Two-factor authentication turned off.',
    required: 'Two-factor authentication is required for your account.',
    back: 'Back to your account',
  },
] as const

for (const copy of COPY) {
  test.describe(copy.locale, () => {
    test.use({ locale: copy.locale })

    test('participante ativa voluntariamente com QR e pode cancelar ou confirmar a desativação', async ({
      page,
    }, info) => {
      if (info.project.name === 'mobile-chromium') await page.setViewportSize({ width: 320, height: 800 })
      const state = await accountApi(page)
      await page.goto('/account')
      await page.getByRole('link', { name: copy.enable, exact: true }).click()
      await page.getByRole('button', { name: copy.start, exact: true }).click()
      await expect(page.getByRole('img')).toBeVisible()
      await expect(page.getByText('SYNTHETICKEY', { exact: true })).toBeVisible()
      await page.getByRole('link', { name: copy.back, exact: true }).click()
      await expect(page.getByRole('link', { name: copy.enable, exact: true })).toBeVisible()
      await expect(page.getByText('SYNTHETICKEY', { exact: true })).toHaveCount(0)
      await page.getByRole('link', { name: copy.enable, exact: true }).click()
      await page.getByRole('button', { name: copy.start, exact: true }).click()
      await page.getByLabel(copy.code, { exact: true }).fill('123456')
      await page.getByRole('button', { name: copy.activate, exact: true }).click()
      await page.getByRole('button', { name: copy.saved, exact: true }).click()
      await expect(page).toHaveURL(/\/account$/)
      await page.getByRole('button', { name: copy.disable, exact: true }).click()
      await expect(page.getByRole('group', { name: copy.group, exact: true })).toBeFocused()
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()).violations,
      ).toEqual([])
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
      await page.getByRole('button', { name: copy.cancel, exact: true }).click()
      await expect(page.getByRole('button', { name: copy.disable, exact: true })).toBeFocused()
      expect(state.disabled).toBe(0)
      await page.getByRole('button', { name: copy.disable, exact: true }).click()
      await page.getByRole('button', { name: copy.confirm, exact: true }).click()
      await expect(page.getByRole('status').filter({ hasText: copy.done })).toContainText(copy.done)
      await expect(page.getByRole('status').filter({ hasText: copy.done })).toBeFocused()
      await expect(page.getByRole('link', { name: copy.enable, exact: true })).toBeVisible()
      expect(state.disabled).toBe(1)
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()).violations,
      ).toEqual([])
      const stored = await page.evaluate(() =>
        JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage), location.href]),
      )
      expect(stored).not.toMatch(/SYNTHETICKEY|synthetic-optional-recovery|otpauth:/)
    })

    test('MFA obrigatório de contas privilegiadas não oferece desativação nem saída do cadastro', async ({ page }) => {
      for (const roles of [['operator'], ['product_admin'], ['moderator'], ['participant', 'moderator']]) {
        await page.unroute('**/api/v1/**')
        await accountApi(page, roles, true)
        await page.goto('/account')
        await expect(page.getByText(copy.required, { exact: true })).toBeVisible()
        await expect(page.getByRole('button', { name: copy.disable, exact: true })).toHaveCount(0)
        await page.unroute('**/api/v1/**')
        await accountApi(page, roles, false)
        await page.reload()
        await expect(page).toHaveURL(/\/account\/enable-mfa$/)
        await expect(page.getByRole('button', { name: copy.start, exact: true })).toBeVisible()
        await expect(page.getByRole('link', { name: copy.back, exact: true })).toHaveCount(0)
      }
    })
  })
}
