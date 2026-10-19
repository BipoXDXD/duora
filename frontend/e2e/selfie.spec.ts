import { expect, test } from '@playwright/test'
import { AxeBuilder } from '@axe-core/playwright'
import { FakeDuoraApi } from './support/fake-api.ts'

test('selfie: instruções acessíveis, indisponibilidade e saída sem documentos', async ({ page }) => {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser('selfie@example.com', 'frase longa de senha de teste')
  await page.route('**/api/v1/me/selfie-verifications/current', (route) =>
    route.fulfill({ json: { id: null, state: 'NotStarted', nextStep: 'Start', feedbackCode: null, expiresAt: null } }),
  )
  await page.route('**/api/v1/me/selfie-verifications', (route) =>
    route.fulfill({
      status: 503,
      contentType: 'application/problem+json',
      json: { status: 503, title: 'Unavailable', code: 'SELFIE_UNAVAILABLE' },
    }),
  )
  await page.goto('/entrar')
  await page.getByLabel('E-mail', { exact: true }).fill('selfie@example.com')
  await page.getByLabel('Senha', { exact: true }).fill('frase longa de senha de teste')
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByText('selfie@example.com')).toBeVisible()
  await page.goto('/conta/selfie')
  await expect(page.getByRole('heading', { name: 'Confirmar presença' })).toBeVisible()
  await expect(page.getByText(/Não estimamos sua idade/)).toBeVisible()
  const start = page.getByRole('button', { name: 'Iniciar captura' })
  await start.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('alert')).toBeVisible()
  expect(
    (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()).violations,
  ).toEqual([])
  await page.getByRole('link', { name: 'Voltar à conta' }).click()
  await expect(page.getByText('selfie@example.com')).toBeVisible()
  expect(await page.evaluate(() => Object.keys(sessionStorage))).not.toContain('selfie')
})
