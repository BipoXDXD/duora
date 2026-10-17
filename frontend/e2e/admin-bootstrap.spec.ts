import { AxeBuilder } from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

test('senha temporária → senha própria → MFA, inclusive reload e navegação direta', async ({ page, context }) => {
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
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByLabel('Código do autenticador').fill('123456')
  await page.getByRole('button', { name: 'Ativar MFA' }).click()
  await expect(page.getByText('synthetic-recovery')).toBeVisible()
  await page.getByRole('button', { name: 'Guardei os códigos, continuar' }).click()
  await expect(page).toHaveURL(/conta$/)
  await expect(page.getByText('synthetic-recovery')).toHaveCount(0)
})
