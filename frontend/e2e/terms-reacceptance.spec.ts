import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { syntheticLegalCatalog } from '../src/test/legal-documents.js'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']
const PASSWORD = 'frase longa de senha de teste'

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function signInWithOlderTerms(
  page: Page,
  email: string,
  labels: { email: string; password: string; submit: string },
) {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser(email, PASSWORD)
  api.seedOlderTermsAcceptance(email)
  await page.goto('/login')
  await page.getByLabel(labels.email, { exact: true }).fill(email)
  await page.getByLabel(labels.password, { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: labels.submit }).click()
  return api
}

test.describe('novo aceite dos Termos (#296)', () => {
  test('aviso não bloqueante → tela de reaceite → aceite com 18+ → aviso some', async ({ page }) => {
    const api = await signInWithOlderTerms(page, 'ana@exemplo.com', {
      email: 'E-mail',
      password: 'Senha',
      submit: 'Entrar',
    })

    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
    const notice = page.getByRole('complementary', { name: 'Os Termos de uso mudaram' })
    await expect(notice).toBeVisible()
    await expectNoAxeViolations(page)

    // O aviso não bloqueia: a área continua navegável sem aceitar.
    await page.getByRole('link', { name: 'Editar perfil' }).click()
    await expect(page).toHaveURL(/\/account\/profile$/)
    await expect(notice).toBeVisible()

    await notice.getByRole('link', { name: 'Revisar e aceitar' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Novo aceite dos Termos' })).toBeVisible()
    await expect(notice).toBeHidden()
    const termsLink = page.getByRole('link', { name: /^Ler Synthetic Terms/ })
    await expect(termsLink).toHaveAttribute('href', `/terms/${syntheticLegalCatalog.termsVersion}/pt-BR`)
    await expectNoAxeViolations(page)

    // Sem a declaração de 18+, nada é enviado.
    await page.getByRole('checkbox', { name: /Li e aceito os Termos de uso/ }).check()
    await page.getByRole('button', { name: 'Confirmar aceite' }).click()
    await expect(page.getByText('Para registrar o novo aceite, declare que tem 18 anos ou mais.')).toBeVisible()
    expect(api.termsAcceptances).toEqual([])

    await page.getByRole('checkbox', { name: 'Declaro ter 18 anos ou mais' }).check()
    await page.getByRole('button', { name: 'Confirmar aceite' }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Aceite registrado. Obrigado!' })).toBeFocused()
    expect(api.termsAcceptances).toEqual([
      {
        acceptedTermsVersion: syntheticLegalCatalog.termsVersion,
        acceptedTermsLocale: 'pt-BR',
        acceptedTermsSha256: syntheticLegalCatalog.documents.find(
          (document) => document.document === 'terms' && document.locale === 'pt-BR',
        )?.sha256,
        confirmedAdult: 'true',
      },
    ])

    await page.getByRole('link', { name: 'Voltar para a conta' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
    await expect(notice).toBeHidden()
    // Uma nova leitura de /me (recarregar) confirma o estado do servidor, não só o do cache.
    await page.reload()
    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
    await expect(notice).toBeHidden()
  })

  test('o aviso pode ser dispensado e devolve o foco ao conteúdo', async ({ page }) => {
    await signInWithOlderTerms(page, 'ana@exemplo.com', { email: 'E-mail', password: 'Senha', submit: 'Entrar' })
    const notice = page.getByRole('complementary', { name: 'Os Termos de uso mudaram' })
    await expect(notice).toBeVisible()

    await notice.getByRole('button', { name: 'Dispensar aviso' }).click()

    await expect(notice).toBeHidden()
    await expect(page.getByRole('main')).toBeFocused()
  })
})

test.describe('updated Terms in English (#296)', () => {
  test.use({ locale: 'en-US' })

  test('notice and acceptance screen are translated and accessible', async ({ page }) => {
    await signInWithOlderTerms(page, 'ana@example.com', { email: 'Email', password: 'Password', submit: 'Sign in' })

    const notice = page.getByRole('complementary', { name: 'The Terms of Use have changed' })
    await expect(notice).toBeVisible()
    await notice.getByRole('link', { name: 'Review and accept' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Updated Terms of Use' })).toBeVisible()
    await expect(page.getByRole('checkbox', { name: 'I declare that I am 18 or older' })).toBeVisible()
    await expectNoAxeViolations(page)
  })
})
