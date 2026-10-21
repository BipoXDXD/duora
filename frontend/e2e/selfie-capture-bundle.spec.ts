import { expect, test } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

test('captura: componente distribuído mantém o estado de espera e permite sair', async ({ page }) => {
  // A permissão fica pendente: nenhum dispositivo ou serviço externo é utilizado.
  await page.addInitScript(() => {
    Object.defineProperty(MediaDevices.prototype, 'getUserMedia', {
      configurable: true,
      value: () =>
        new Promise<MediaStream>(() => {
          /* permissão pendente */
        }),
    })
  })
  await page.route('https://**', (route) => route.abort())
  const errors: string[] = []
  page.on('pageerror', (error) => errors.push(error.message))
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser('capture@example.com', 'frase longa de senha de teste')
  const id = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
  await page.route('**/api/v1/me/selfie-verifications/current', (route) =>
    route.fulfill({ json: { id, state: 'Capturing', nextStep: 'Capture', feedbackCode: null, expiresAt: null } }),
  )
  await page.route(`**/api/v1/me/selfie-verifications/${id}/capture`, (route) =>
    route.fulfill({
      json: {
        sessionId: id,
        region: 'us-east-1',
        credentials: {
          accessKeyId: 'synthetic',
          secretAccessKey: 'synthetic',
          sessionToken: 'synthetic',
          expiration: '2099-01-01T00:00:00Z',
        },
      },
    }),
  )
  await page.goto('/login')
  await page.getByLabel('E-mail', { exact: true }).fill('capture@example.com')
  await page.getByLabel('Senha', { exact: true }).fill('frase longa de senha de teste')
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByText('capture@example.com')).toBeVisible()
  await page.goto('/account/selfie')
  await page.getByRole('button', { name: 'Abrir câmera' }).click()
  await expect(page.getByText('Aguardando sua permissão para usar a câmera')).toBeVisible()
  await expect(page.locator('.liveness-detector')).toBeVisible()
  await expect(page.locator('.amplify-loader').first()).toBeVisible()
  await expect(page.locator('.amplify-liveness-start-screen-camera-waiting')).toHaveCSS('display', 'flex')
  await page.getByRole('link', { name: 'Voltar à conta' }).click()
  await expect(page.getByText('capture@example.com')).toBeVisible()
  expect(errors).toEqual([])
})
