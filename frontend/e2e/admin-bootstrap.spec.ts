import { AxeBuilder } from '@axe-core/playwright'
import { type BrowserContext, expect, type Page, test } from '@playwright/test'

/** API do bootstrap simulada: senha temporária → senha própria → MFA. Devolve os corpos da troca de senha. */
async function mockBootstrapApi(page: Page, context: BrowserContext): Promise<unknown[]> {
  let phase = 'temporary'
  let csrf = 0
  const received: unknown[] = []
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    const problem = (status: number, code: string) =>
      route.fulfill({ status, contentType: 'application/problem+json', json: { title: code, code, status } })
    if (path.endsWith('/auth/csrf')) {
      csrf++
      await context.addCookies([{ name: 'XSRF-TOKEN', value: `csrf-${String(csrf)}`, url: 'http://127.0.0.1:4173' }])
      await route.fulfill({ status: 204 })
      return
    }
    if (request.method() === 'POST') expect(request.headers()['x-xsrf-token']).toBe(`csrf-${String(csrf)}`)
    if (path.endsWith('/auth/login')) {
      await route.fulfill({ status: 204 })
      return
    }
    if (path.endsWith('/me')) {
      if (phase === 'temporary') {
        await problem(403, 'PASSWORD_CHANGE_REQUIRED')
        return
      }
      await route.fulfill({
        json: {
          id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
          email: 'admin@example.test',
          emailConfirmed: true,
          status: 'Active',
          roles: ['operator'],
          mfaEnabled: phase === 'complete',
        },
      })
      return
    }
    if (path.endsWith('/password-changes')) {
      received.push(request.postDataJSON())
      phase = 'mfa'
      await route.fulfill({ status: 204 })
      return
    }
    if (path.endsWith('/mfa/enrollments')) {
      await route.fulfill({ json: { sharedKey: 'SYNTHETICKEY', authenticatorUri: 'otpauth://totp/test' } })
      return
    }
    if (path.endsWith('/mfa/confirmations')) {
      expect(request.postDataJSON()).toEqual({ code: '123456' })
      phase = 'complete'
      await route.fulfill({ json: { recoveryCodes: ['synthetic-recovery'] } })
      return
    }
    if (path.endsWith('/sessions')) {
      await route.fulfill({ json: { items: [] } })
      return
    }
    await route.fulfill({ status: 404 })
  })
  return received
}

test('senha temporária → senha própria → MFA, inclusive reload e navegação direta', async ({ page, context }) => {
  const received = await mockBootstrapApi(page, context)
  await page.goto('/conta/perfil')
  await expect(page).toHaveURL(/trocar-senha-inicial$/)
  await page.reload()
  await expect(page.getByLabel('Senha temporária')).toBeVisible()
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByLabel('Senha temporária').fill('SYNTHETIC-TEMPORARY')
  await page.getByLabel('Senha nova', { exact: true }).fill(' minha senha longa própria ')
  await page.getByLabel('Confirme a nova senha').fill(' minha senha longa própria ')
  await page.getByRole('button', { name: 'Salvar senha nova' }).click()
  await expect(page).toHaveURL(/conta\/ativar-mfa$/)
  expect(received).toEqual([{ currentPassword: 'SYNTHETIC-TEMPORARY', newPassword: ' minha senha longa própria ' }])
  await page.getByRole('button', { name: 'Configurar autenticador' }).click()
  await expect(page.getByText('SYNTHETICKEY')).toBeVisible()
  await expect(page.locator(':focus')).toContainText('SYNTHETICKEY')
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByLabel('Código do autenticador').fill('123456')
  await page.getByRole('button', { name: 'Ativar MFA' }).click()
  await expect(page.getByText('synthetic-recovery')).toBeVisible()
  await expect(page.getByRole('status')).toBeFocused()
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByRole('button', { name: 'Guardei os códigos, continuar' }).click()
  await expect(page).toHaveURL(/conta$/)
  await expect(page.getByText('synthetic-recovery')).toHaveCount(0)
})

test.describe('jornada do bootstrap em inglês (navegador em en-US)', () => {
  test.use({ locale: 'en-US' })

  test('initial password → own password → MFA, com axe em cada tela', async ({ page, context }) => {
    const received = await mockBootstrapApi(page, context)
    await page.goto('/conta')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
    await expect(page.getByRole('heading', { level: 1, name: 'Change your initial password' })).toBeVisible()
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByLabel('Temporary password').fill('SYNTHETIC-TEMPORARY')
    await page.getByLabel('New password', { exact: true }).fill(' my own long password ')
    await page.getByLabel('Confirm new password').fill(' my own long password ')
    await page.getByRole('button', { name: 'Save new password' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Enable two-factor authentication' })).toBeVisible()
    expect(received).toEqual([{ currentPassword: 'SYNTHETIC-TEMPORARY', newPassword: ' my own long password ' }])
    await page.getByRole('button', { name: 'Set up authenticator' }).click()
    await expect(page.getByText('SYNTHETICKEY')).toBeVisible()
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByLabel('Authenticator code').fill('123456')
    await page.getByRole('button', { name: 'Enable MFA' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Save your recovery codes' })).toBeVisible()
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByRole('button', { name: 'I saved the codes, continue' }).click()
    await expect(page).toHaveURL(/conta$/)
  })
})
