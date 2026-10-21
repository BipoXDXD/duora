import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags; não existe tag wcag22a com regras.
// Ao atualizar o axe, confira a lista de tags antes de ampliar ou reduzir a cobertura.
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

// Os chunks das telas levam o nome do módulo (LoginPage-<hash>.js); a página inicial não pode puxá-los.
const FORM_SCREEN_CHUNK =
  /\/(Register|CheckEmail|ConfirmEmail|Login|ForgotPassword|ResetPassword|Account)Page-|\/RequireSession-/

/** Caminhos dos chunks JS pedidos pela página, filtrados pela URL (o tipo do recurso varia entre navegadores). */
function recordChunkRequests(page: Page): string[] {
  const chunks: string[] = []
  page.on('request', (request) => {
    const { pathname } = new URL(request.url())
    if (pathname.startsWith('/assets/') && pathname.endsWith('.js')) {
      chunks.push(pathname)
    }
  })
  return chunks
}

test.describe('página inicial', () => {
  test('abre com título, landmarks e nome do produto', async ({ page }) => {
    await page.goto('/')

    await expect(page).toHaveTitle('Duora')
    await expect(page.getByRole('heading', { level: 1, name: 'Duora' })).toBeVisible()
    await expect(page.getByRole('banner')).toBeVisible()
    await expect(page.getByRole('main')).toBeVisible()
    await expect(page.getByRole('contentinfo')).toBeVisible()
  })

  test('não manda a URL da SPA no Referer', async ({ page }) => {
    await page.goto('/')

    await expect(page.locator('meta[name="referrer"]')).toHaveAttribute('content', 'no-referrer')
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

  test('baixa só o código da página inicial; as telas de formulário vêm sob demanda (spec 13.5)', async ({ page }) => {
    const chunks = recordChunkRequests(page)
    await page.goto('/')
    await expect(page.getByRole('heading', { level: 1, name: 'Duora' })).toBeVisible()

    expect(chunks.filter((path) => FORM_SCREEN_CHUNK.test(path))).toEqual([])

    await page.getByRole('main').getByRole('link', { name: 'Entrar' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeFocused()
    expect(chunks.some((path) => path.includes('/LoginPage-'))).toBe(true)
  })

  test('chunk que some no servidor (deploy novo): uma recarga só e depois o aviso de versão nova', async ({ page }) => {
    const chunks = recordChunkRequests(page)
    let documentLoads = 0
    page.on('request', (request) => {
      if (request.isNavigationRequest()) {
        documentLoads += 1
      }
    })
    await page.route('**/assets/LoginPage-*.js', (route) => route.fulfill({ status: 404, body: 'não encontrado' }))

    await page.goto('/login')

    await expect(page.getByRole('heading', { level: 1, name: 'Há uma versão nova do Duora' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Recarregar a página' })).toBeVisible()
    // Sem laço: um download repetido nunca deixa a rede ociosa, e este passo estoura o timeout.
    await page.waitForLoadState('networkidle')
    expect(documentLoads).toBe(2)
    expect(chunks.filter((path) => path.includes('/LoginPage-')).length).toBeLessThanOrEqual(2)
  })

  test('rota inexistente também passa no axe', async ({ page }) => {
    await page.goto('/nao-existe')
    await expect(page).toHaveTitle('Página não encontrada · Duora')

    const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()

    expect(results.violations).toEqual([])
  })
})
