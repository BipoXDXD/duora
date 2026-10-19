import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

const PASSWORD = 'a long test passphrase here'

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function signIn(page: Page, email: string): Promise<void> {
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Password', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

test.describe('interface em inglês (navegador em en-US)', () => {
  test.use({ locale: 'en-US' })

  test('sign up → check email → confirm → sign in → account → sign out, com axe em cada tela', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    await page.goto('/')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US')
    await expect(page.getByRole('heading', { level: 1, name: 'Duora' })).toBeVisible()
    await expectNoAxeViolations(page)

    await page.getByRole('main').getByRole('link', { name: 'Create account' }).click()
    await expect(page).toHaveTitle('Create account · Duora')
    await expect(page.getByRole('heading', { level: 1, name: 'Create account' })).toBeFocused()
    await page.getByRole('button', { name: 'Create account' }).click()
    await expect(page.getByText('Enter your email.')).toBeVisible()
    await expect(page.getByText('To create an account, please accept the Terms of Use.')).toBeVisible()
    await expectNoAxeViolations(page)

    await page.getByLabel('Email', { exact: true }).fill('ana@example.com')
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD)
    await page.getByRole('checkbox', { name: /Terms of Use/ }).check()
    await page.getByRole('checkbox', { name: /Privacy Policy/ }).check()
    await page.getByRole('button', { name: 'Create account' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Check your email' })).toBeVisible()
    await expect(page.getByText("If an account can be created with that address, you'll get an email")).toBeVisible()
    await expectNoAxeViolations(page)

    await page.goto(api.lastEmailLink('confirmation').path)
    await expect(page).toHaveTitle('Confirm email · Duora')
    await page.getByLabel('Password', { exact: true }).fill('not the sign-up password')
    await page.getByRole('button', { name: 'Confirm email' }).click()
    await expect(page.getByRole('alert')).toContainText('Invalid link or incorrect password.')
    await expectNoAxeViolations(page)
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Confirm email' }).click()
    await expect(page.getByRole('status')).toContainText('Email confirmed.')

    await page.getByRole('main').getByRole('link', { name: 'Sign in' }).click()
    await expect(page).toHaveTitle('Sign in · Duora')
    await signIn(page, 'ana@example.com')

    await expect(page.getByRole('heading', { level: 1, name: 'Your account' })).toBeVisible()
    await expect(page.getByText('Active', { exact: true })).toBeVisible()
    await expect(page.getByText('This session', { exact: true })).toBeVisible()
    await expect(page.getByTestId('anuncio-de-rota')).toHaveText('Page loaded: Your account · Duora')
    await expectNoAxeViolations(page)

    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText("You've signed out.")
    await expectNoAxeViolations(page)
  })

  test('mensagens de entrada e de recuperação continuam neutras em inglês', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('ana@example.com', PASSWORD)

    await page.goto('/entrar')
    await page.getByLabel('Email', { exact: true }).fill('nobody@example.com')
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toContainText('Incorrect email or password.')
    await expectNoAxeViolations(page)

    const sentMessages: string[] = []
    for (const email of ['ana@example.com', 'nobody@example.com']) {
      await page.goto('/esqueci-a-senha')
      await page.getByLabel('Email', { exact: true }).fill(email)
      await page.getByRole('button', { name: 'Send link' }).click()
      sentMessages.push(await page.getByRole('status').innerText())
    }
    expect(sentMessages[0]).toContain('If there is an account with that email')
    expect(sentMessages[1]).toBe(sentMessages[0])
  })

  test('rota inexistente em inglês', async ({ page }) => {
    await page.goto('/nao-existe')

    await expect(page).toHaveTitle('Page not found · Duora')
    await expect(page.getByRole('main').getByRole('link', { name: 'Back to home' })).toBeVisible()
    await expectNoAxeViolations(page)
  })
})

test.describe('seletor de idioma', () => {
  test('troca para inglês pelo teclado, mantém a escolha ao recarregar e volta ao português', async ({
    page,
    browserName,
  }) => {
    // No WebKit, como no Safari padrão, Tab não alcança links; Option+Tab alcança (ver smoke.spec.ts).
    const nextKey = browserName === 'webkit' ? 'Alt+Tab' : 'Tab'
    await page.goto('/entrar')
    await expect(page.getByRole('banner').getByRole('group', { name: 'Idioma' })).toBeVisible()
    const english = page.getByRole('banner').getByRole('button', { name: 'English' })
    await expect(english).toHaveAttribute('aria-pressed', 'false')

    for (let presses = 0; presses < 10 && !(await english.evaluate((el) => el === document.activeElement)); presses++) {
      await page.keyboard.press(nextKey)
    }
    await expect(english).toBeFocused()
    await page.keyboard.press('Enter')

    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US')
    await expect(page).toHaveTitle('Sign in · Duora')
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
    await expect(page.getByRole('banner').getByRole('group', { name: 'Language' })).toBeVisible()
    await expect(english).toHaveAttribute('aria-pressed', 'true')

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US')
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()

    await page.getByRole('banner').getByRole('button', { name: 'Português' }).click()
    await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR')
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
  })
})
