import {
  withSessionContext,
  passwordChangeBootstrap,
  SYNTHETIC_SESSION_ID,
  SYNTHETIC_RENEWED_SESSION_ID,
} from './support/private-context.ts'
import jsQR from 'jsqr'
import { AxeBuilder } from '@axe-core/playwright'
import { type BrowserContext, expect, type Page, test } from '@playwright/test'

const SHARED_KEY = 'SYNTHETICKEY'
const AUTHENTICATOR_URI = `otpauth://totp/Duora:admin%2Bqr%40example.test?secret=${SHARED_KEY}&issuer=Duora&digits=6&period=30`

/** Decodifica o SVG real, rasterizado pelo navegador; não substitui o gerador por mock. */
async function expectQrContent(page: Page, label: string): Promise<void> {
  const qr = page.getByRole('img', { name: label })
  await expect(qr).toBeVisible()
  const pixels = await qr.evaluate(async (svg) => {
    const canvas = document.createElement('canvas')
    canvas.width = 512
    canvas.height = 512
    const context = canvas.getContext('2d')
    if (context === null) throw new Error('Canvas indisponível no teste')
    const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(svg)], { type: 'image/svg+xml' }))
    try {
      const image = new Image()
      image.src = url
      await image.decode()
      context.drawImage(image, 0, 0, canvas.width, canvas.height)
      return Array.from(context.getImageData(0, 0, canvas.width, canvas.height).data)
    } finally {
      URL.revokeObjectURL(url)
    }
  })
  expect(jsQR.default(new Uint8ClampedArray(pixels), 512, 512)?.data).toBe(AUTHENTICATOR_URI)
  const bounds = await qr.boundingBox()
  expect(bounds?.width).toBeGreaterThanOrEqual(200)
  expect(bounds?.width).toBeLessThanOrEqual(256)
}

/** API do bootstrap simulada: senha temporária → senha própria → MFA. Devolve os corpos da troca de senha. */
async function mockBootstrapApi(page: Page, context: BrowserContext): Promise<unknown[]> {
  let phase = 'temporary'
  let csrf = 0
  const received: unknown[] = []
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (path.endsWith('/auth/csrf')) {
      csrf++
      await context.addCookies([{ name: 'XSRF-TOKEN', value: `csrf-${String(csrf)}`, url: 'http://127.0.0.1:4173' }])
      await route.fulfill({ status: 204 })
      return
    }
    if (request.method() === 'POST')
      expect(request.headers()['x-xsrf-token']).toBe(
        path.endsWith('/auth/login') ? `csrf-${String(csrf)}` : 'synthetic',
      )
    if (path.endsWith('/auth/login')) {
      await route.fulfill({ status: 204 })
      return
    }
    if (path.endsWith('/me')) {
      if (phase === 'temporary') {
        await route.fulfill({
          status: 403,
          contentType: 'application/problem+json',
          json: passwordChangeBootstrap('0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'),
        })
        return
      }
      await route.fulfill({
        json: withSessionContext(
          {
            id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
            email: 'admin@example.test',
            emailConfirmed: true,
            status: 'Active',
            roles: ['operator'],
            mfaEnabled: phase === 'complete',
            // Defensivo: mesmo com false, a conta privilegiada não vê o aviso dos Termos de participante (#296).
            currentTermsAccepted: false,
          },
          phase === 'complete' ? SYNTHETIC_RENEWED_SESSION_ID : SYNTHETIC_SESSION_ID,
        ),
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
      await route.fulfill({ json: { sharedKey: 'SYNTHETICKEY', authenticatorUri: AUTHENTICATOR_URI } })
      return
    }
    if (path.endsWith('/mfa/confirmations')) {
      expect(request.postDataJSON()).toEqual({ code: '123456' })
      phase = 'complete'
      await route.fulfill({
        json: { recoveryCodes: ['synthetic-recovery'], resultSessionId: SYNTHETIC_RENEWED_SESSION_ID },
      })
      return
    }
    if (path.endsWith('/sessions')) {
      await route.fulfill({ json: { items: [] } })
      return
    }
    await route.fulfill({ status: 404 })
  })
  return received
}

test('senha temporária → senha própria → MFA, inclusive reload e navegação direta', async ({ page, context }) => {
  const outgoing: string[] = []
  const logs: string[] = []
  page.on('request', (request) => outgoing.push(JSON.stringify([request.url(), request.postData(), request.headers()])))
  page.on('console', (message) => logs.push(message.text()))
  const received = await mockBootstrapApi(page, context)
  await page.goto('/account/profile')
  await expect(page).toHaveURL(/change-initial-password$/)
  // O reload faz parte do comportamento testado, mas só depois da tela pronta: goto retorna no load com o chunk lazy da
  // rota ainda baixando, e o WebKit interrompe a navegação ("Frame load interrupted").
  await expect(page.getByLabel('Senha temporária')).toBeVisible()
  await page.reload()
  await expect(page.getByLabel('Senha temporária')).toBeVisible()
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByLabel('Senha temporária').fill('SYNTHETIC-TEMPORARY')
  await page.getByLabel('Senha nova', { exact: true }).fill(' minha senha longa própria ')
  await page.getByLabel('Confirme a nova senha').fill(' minha senha longa própria ')
  await page.getByRole('button', { name: 'Salvar senha nova' }).click()
  await expect(page).toHaveURL(/account\/enable-mfa$/)
  expect(received).toEqual([{ currentPassword: 'SYNTHETIC-TEMPORARY', newPassword: ' minha senha longa própria ' }])
  await page.getByRole('button', { name: 'Configurar autenticador' }).click()
  await expect(page.getByText('SYNTHETICKEY')).toBeVisible()
  await expect(page.locator(':focus')).toContainText('SYNTHETICKEY')
  await expectQrContent(page, 'QR Code para configurar o autenticador')
  await page.getByRole('link', { name: 'Duora', exact: true }).click()
  // A URL pode mudar antes de a transição React desmontar a tela privada. Espere a saída efetiva antes de voltar.
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByText(SHARED_KEY)).toHaveCount(0)
  await page.goBack()
  await expect(page.getByText(SHARED_KEY)).toHaveCount(0)
  await expect(page.getByRole('img', { name: 'QR Code para configurar o autenticador' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Configurar autenticador' }).click()
  await expectQrContent(page, 'QR Code para configurar o autenticador')
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByLabel('Código do autenticador').fill('123456')
  await page.getByRole('button', { name: 'Ativar MFA' }).click()
  await expect(page.getByText('synthetic-recovery')).toBeVisible()
  await expect(page.getByText(SHARED_KEY)).toHaveCount(0)
  await expect(page.getByRole('img', { name: 'QR Code para configurar o autenticador' })).toHaveCount(0)
  await expect(page.getByRole('status')).toBeFocused()
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  await page.getByRole('button', { name: 'Guardei os códigos, continuar' }).click()
  await expect(page).toHaveURL(/account$/)
  await expect(page.getByText('synthetic-recovery')).toHaveCount(0)
  await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
  await expect(page.getByRole('complementary', { name: 'Os Termos de uso mudaram' })).toHaveCount(0)
  const storage = await page.evaluate(() =>
    JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]),
  )
  for (const traces of [outgoing.join('\n'), logs.join('\n'), storage]) {
    expect(traces).not.toContain(SHARED_KEY)
    expect(traces).not.toContain('otpauth://')
  }
})

test.describe('jornada do bootstrap em inglês (navegador em en-US)', () => {
  test.use({ locale: 'en-US' })

  test('initial password → own password → MFA, com axe em cada tela', async ({ page, context }) => {
    const received = await mockBootstrapApi(page, context)
    await page.goto('/account')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US')
    await expect(page.getByRole('heading', { level: 1, name: 'Change your initial password' })).toBeVisible()
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByLabel('Temporary password').fill('SYNTHETIC-TEMPORARY')
    await page.getByLabel('New password', { exact: true }).fill(' my own long password ')
    await page.getByLabel('Confirm new password').fill(' my own long password ')
    await page.getByRole('button', { name: 'Save new password' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Enable two-factor authentication' })).toBeVisible()
    expect(received).toEqual([{ currentPassword: 'SYNTHETIC-TEMPORARY', newPassword: ' my own long password ' }])
    await page.getByRole('button', { name: 'Set up authenticator' }).click()
    await expect(page.getByText('SYNTHETICKEY')).toBeVisible()
    await expectQrContent(page, 'QR code to set up your authenticator')
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByLabel('Authenticator code').fill('123456')
    await page.getByRole('button', { name: 'Enable MFA' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Save your recovery codes' })).toBeVisible()
    await expect(page.getByText(SHARED_KEY)).toHaveCount(0)
    await expect(page.getByRole('img', { name: 'QR code to set up your authenticator' })).toHaveCount(0)
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    await page.getByRole('button', { name: 'I saved the codes, continue' }).click()
    await expect(page).toHaveURL(/account$/)
  })
})
