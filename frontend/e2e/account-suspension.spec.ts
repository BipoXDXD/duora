import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']
const PASSWORD = 'frase longa de senha de teste'

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function signInSuspended(page: Page, email: string, labels: { email: string; password: string; submit: string }) {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser(email, PASSWORD)
  api.seedSuspension(email, '2026-10-30T12:00:00Z')
  await page.goto('/login')
  await page.getByLabel(labels.email, { exact: true }).fill(email)
  await page.getByLabel(labels.password, { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: labels.submit }).click()
  return api
}

test.describe('conta suspensa (#299)', () => {
  test('entra, vê a tela da suspensão e não alcança outras áreas', async ({ page }) => {
    await signInSuspended(page, 'ana@exemplo.com', { email: 'E-mail', password: 'Senha', submit: 'Entrar' })

    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page).toHaveURL(/\/account\/suspended$/)
    await expect(page.getByText('Violação dos Termos de uso')).toBeVisible()
    // Logo depois do login a pessoa não pediu outra área: sem o aviso de área indisponível.
    await expect(page.getByText('Esta área não está disponível para a sua conta agora.')).toBeHidden()
    await expect(page.getByRole('heading', { level: 2, name: 'Sessões ativas' })).toBeVisible()
    await expectNoAxeViolations(page)

    await page.goto('/events')
    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page).toHaveURL(/\/account\/suspended$/)
    await expect(page.getByText('Esta área não está disponível para a sua conta agora.')).toBeVisible()
  })

  test('o login que volta para outra área não mostra o aviso de área indisponível', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('eva@exemplo.com', PASSWORD)
    api.seedSuspension('eva@exemplo.com', '2026-10-30T12:00:00Z')
    await page.goto('/events')
    await expect(page).toHaveURL(/\/login$/)
    await page.getByLabel('E-mail', { exact: true }).fill('eva@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Entrar' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page.getByText('Esta área não está disponível para a sua conta agora.')).toBeHidden()
  })

  test('a marca do login é consumida: recarregar outra área depois da suspensão mostra o aviso', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('fabi@exemplo.com', PASSWORD)
    await page.goto('/events')
    await expect(page).toHaveURL(/\/login$/)
    await page.getByLabel('E-mail', { exact: true }).fill('fabi@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Entrar' }).click()
    await expect(page).toHaveURL(/\/events$/)

    api.seedSuspension('fabi@exemplo.com', '2026-10-30T12:00:00Z')
    await page.reload()

    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page.getByText('Esta área não está disponível para a sua conta agora.')).toBeVisible()
  })

  test('alcança as telas da allowlist a partir da tela de suspensão', async ({ page }) => {
    await signInSuspended(page, 'caio@exemplo.com', { email: 'E-mail', password: 'Senha', submit: 'Entrar' })

    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Autenticação em duas etapas' })).toHaveAttribute(
      'href',
      '/account/enable-mfa',
    )
    await page.getByRole('link', { name: 'Minhas fotos' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Minhas fotos' })).toBeVisible()
    await expect(page).toHaveURL(/\/account\/photos$/)
    await expectNoAxeViolations(page)
  })

  test('suspensa com a sessão aberta, a próxima ação leva à tela da suspensão', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('dani@exemplo.com', PASSWORD)
    await page.goto('/login')
    await page.getByLabel('E-mail', { exact: true }).fill('dani@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Entrar' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()

    api.seedSuspension('dani@exemplo.com', '2026-10-30T12:00:00Z')
    await page.getByRole('link', { name: 'Editar perfil' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Conta suspensa' })).toBeVisible()
    await expect(page.getByText('Esta área não está disponível para a sua conta agora.')).toBeVisible()
  })
})

test.describe('suspended account in English (#299)', () => {
  test.use({ locale: 'en-US' })

  test('the suspension screen is translated and accessible', async ({ page }) => {
    await signInSuspended(page, 'bia@example.com', { email: 'Email', password: 'Password', submit: 'Sign in' })

    await expect(page.getByRole('heading', { level: 1, name: 'Account suspended' })).toBeVisible()
    await expect(page.getByText('Violation of the Terms of Use')).toBeVisible()
    await expectNoAxeViolations(page)
  })
})
