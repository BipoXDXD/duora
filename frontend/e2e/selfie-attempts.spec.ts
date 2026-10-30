import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// Limite de reprovações da selfie (issue #300): estado vindo do servidor, sem câmera nem fornecedor.
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']
const PASSWORD = 'frase longa de senha de teste'

type Attempts = Readonly<{ state: string; remaining: number; lastCycle: boolean; lockedUntil: string | null }>

type Labels = Readonly<{ email: string; password: string; submit: string }>

async function openSelfie(
  page: Page,
  account: Readonly<{ email: string; labels: Labels }>,
  attempts: Attempts,
): Promise<void> {
  const { email, labels } = account
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser(email, PASSWORD)
  await page.route('**/api/v1/me/selfie-verifications/current', (route) =>
    route.fulfill({
      json: {
        id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7',
        state: 'RecaptureRequired',
        nextStep: 'Recapture',
        feedbackCode: 'CAPTURE_QUALITY',
        expiresAt: '2026-10-30T14:00:00Z',
        attempts,
      },
    }),
  )
  await page.goto('/login')
  await page.getByLabel(labels.email, { exact: true }).fill(email)
  await page.getByLabel(labels.password, { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: labels.submit }).click()
  await expect(page.getByText(email)).toBeVisible()
  await page.goto('/account/selfie')
}

async function expectNoAxeViolations(page: Page): Promise<void> {
  expect((await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()).violations).toEqual([])
}

const PT = { email: 'E-mail', password: 'Senha', submit: 'Entrar' }
const EN = { email: 'Email', password: 'Password', submit: 'Sign in' }

test.describe('tentativas da selfie (#300)', () => {
  test('segundo ciclo avisa o bloqueio da conta e mantém a captura', async ({ page }) => {
    await openSelfie(
      page,
      { email: 'ciclo@exemplo.com', labels: PT },
      { state: 'Available', remaining: 2, lastCycle: true, lockedUntil: null },
    )

    await expect(page.getByText('Restam 2 tentativas. Se nenhuma for aprovada, a conta será bloqueada.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Iniciar captura' })).toBeVisible()
    await expectNoAxeViolations(page)
  })

  test('pausa de 1 hora mostra o horário e tira a captura', async ({ page }) => {
    await openSelfie(
      page,
      { email: 'pausa@exemplo.com', labels: PT },
      { state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: '2026-10-30T15:30:00Z' },
    )

    await expect(page.getByText(/Você pode tentar de novo a partir de/)).toBeVisible()
    await expect(page.locator('time[datetime="2026-10-30T15:30:00Z"]')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Iniciar captura' })).toHaveCount(0)
    await expectNoAxeViolations(page)
  })

  test('tentativas esgotadas dizem que a conta foi bloqueada', async ({ page }) => {
    await openSelfie(
      page,
      { email: 'fim@exemplo.com', labels: PT },
      { state: 'Exhausted', remaining: 0, lastCycle: true, lockedUntil: null },
    )

    await expect(page.getByRole('alert')).toContainText('As tentativas acabaram e a conta foi bloqueada.')
    await expect(page.getByRole('button', { name: 'Iniciar captura' })).toHaveCount(0)
    await expectNoAxeViolations(page)
  })
})

test.describe('selfie attempts in English (#300)', () => {
  test.use({ locale: 'en-US' })

  test('the 1-hour pause is translated and accessible', async ({ page }) => {
    await openSelfie(
      page,
      { email: 'pause@example.com', labels: EN },
      { state: 'CoolingDown', remaining: 0, lastCycle: true, lockedUntil: '2026-10-30T15:30:00Z' },
    )

    await expect(page.getByText(/You can try again from/)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Start capture' })).toHaveCount(0)
    await expectNoAxeViolations(page)
  })
})
