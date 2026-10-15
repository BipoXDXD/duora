import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

const EMAIL = 'ana@exemplo.com'
const PASSWORD = 'frase longa de senha de teste'

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function openProfile(page: Page): Promise<FakeDuoraApi> {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser(EMAIL, PASSWORD)
  await page.goto('/entrar')
  await page.getByLabel('E-mail', { exact: true }).fill(EMAIL)
  await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await page.getByRole('link', { name: 'Editar perfil' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Seu perfil' })).toBeVisible()
  return api
}

test.describe('perfil próprio', () => {
  test('conta → editar perfil → salvar → a releitura mostra o que foi salvo', async ({ page }) => {
    await openProfile(page)
    await expectNoAxeViolations(page)

    await page.getByLabel('Nome de exibição').fill('Ana')
    await page.getByLabel('Descrição curta').fill('Gosto de jogos cooperativos.')
    await page.getByRole('button', { name: 'Salvar perfil' }).click()

    await expect(page.getByRole('status').filter({ hasText: 'Perfil salvo.' })).toBeVisible()
    await page.reload()
    await expect(page.getByLabel('Nome de exibição')).toHaveValue('Ana')
    await expect(page.getByLabel('Descrição curta')).toHaveValue('Gosto de jogos cooperativos.')
  })

  test('T-19: outra aba salvou antes → 412 explicado, recarregar mostra a divergência e reaplicar salva', async ({
    page,
  }) => {
    const api = await openProfile(page)
    api.changeProfileElsewhere(EMAIL, { displayName: 'Ana da outra aba' })

    await page.getByLabel('Nome de exibição').fill('Ana Souza')
    await page.getByRole('button', { name: 'Salvar perfil' }).click()

    await expect(page.getByRole('alert')).toContainText('alterado em outra aba ou aparelho')
    await expect(page.getByLabel('Nome de exibição')).toHaveValue('Ana Souza')
    await expectNoAxeViolations(page)

    await page.getByRole('button', { name: 'Recarregar versão atual' }).click()
    await expect(page.getByText('Ana da outra aba')).toBeVisible()
    await page.getByRole('button', { name: 'Salvar perfil' }).click()

    await expect(page.getByRole('status').filter({ hasText: 'Perfil salvo.' })).toBeVisible()
    await page.reload()
    await expect(page.getByLabel('Nome de exibição')).toHaveValue('Ana Souza')
  })

  test('nome vazio é recusado no navegador, com o erro ligado ao campo', async ({ page }) => {
    await openProfile(page)

    await page.getByRole('button', { name: 'Salvar perfil' }).click()

    await expect(page.getByLabel('Nome de exibição')).toHaveAttribute('aria-invalid', 'true')
    await expect(page.getByText('Informe um nome de exibição.')).toBeVisible()
    await expectNoAxeViolations(page)
  })
})
