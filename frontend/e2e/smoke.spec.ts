import { AxeBuilder } from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags; não existe tag wcag22a com regras.
// Ao atualizar o axe, confira a lista de tags antes de ampliar ou reduzir a cobertura.
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

test.describe('página inicial', () => {
  test('abre com título, landmarks e nome do produto', async ({ page }) => {
    await page.goto('/')

    await expect(page).toHaveTitle('Duora')
    await expect(page.getByRole('heading', { level: 1, name: 'Duora' })).toBeVisible()
    await expect(page.getByRole('banner')).toBeVisible()
    await expect(page.getByRole('main')).toBeVisible()
    await expect(page.getByRole('contentinfo')).toBeVisible()
  })

  test('o link de pular conteúdo é o primeiro foco e leva ao conteúdo principal', async ({ page, browserName }) => {
    // No WebKit, como no Safari padrão, Tab não alcança links; Option+Tab alcança.
    const nextLinkKey = browserName === 'webkit' ? 'Alt+Tab' : 'Tab'
    await page.goto('/')

    await page.keyboard.press(nextLinkKey)
    const skipLink = page.getByRole('link', { name: 'Pular para o conteúdo' })
    await expect(skipLink).toBeFocused()
    await expect(skipLink).toBeVisible()

    await page.keyboard.press('Enter')
    await expect(page.getByRole('main')).toBeFocused()
  })

  test('não tem violações automáticas de WCAG 2.2 A/AA', async ({ page }) => {
    await page.goto('/')

    const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()

    expect(results.violations).toEqual([])
  })

  test('rota inexistente também passa no axe', async ({ page }) => {
    await page.goto('/nao-existe')
    await expect(page).toHaveTitle('Página não encontrada · Duora')

    const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()

    expect(results.violations).toEqual([])
  })
})
