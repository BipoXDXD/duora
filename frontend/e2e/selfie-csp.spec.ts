import { readFileSync } from 'node:fs'
import { expect, test } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// Issue #93: o FaceLivenessDetector oficial roda sob a CSP e a Permissions-Policy exatas que a borda dá ao documento
// /account/selfie. Câmera sintética do Chromium e WebSocket interceptado sem servidor: nenhum byte vai à AWS. Prova que
// câmera, vídeo e o StartFaceLivenessSession não são recusados pela política. O WASM e o modelo só carregam depois da
// SessionInformation do servidor; a compilação de WASM e o fetch ao CDN sob essa CSP estão na sonda do Caddy real
// (scripts/deploy/test_caddy_browser.cjs).
const caddy = readFileSync(new URL('../../infra/caddy/Caddyfile', import.meta.url), 'utf8')
const selfiePolicy = /^\s*Content-Security-Policy "([^"]*'wasm-unsafe-eval'[^"]*)"$/m.exec(caddy)?.[1]
const selfiePermissions = /^\s*Permissions-Policy "([^"]*camera=\(self\)[^"]*)"$/m.exec(caddy)?.[1]
test.use({ launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] } })

test('selfie: o componente oficial abre câmera e streaming sob a política exata da borda', async ({
  page,
  baseURL,
}) => {
  if (baseURL === undefined || selfiePolicy === undefined || selfiePermissions === undefined)
    throw new Error('Missing baseURL or the selfie policies in the repository Caddyfile')
  const documentUrl = new URL('/account/selfie', baseURL).href
  const sockets: string[] = []
  const errors: string[] = []
  page.on('pageerror', (error) => errors.push(error.message))
  await page.context().grantPermissions(['camera'], { origin: new URL(baseURL).origin })
  // WebSocket mantido aberto e mudo, sem servidor: nenhum byte vai à AWS.
  await page.routeWebSocket(/^wss:\/\/streaming-rekognition\.us-east-1\.amazonaws\.com\//, (socket) => {
    sockets.push(new URL(socket.url()).host)
  })
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser('csp@example.com', 'frase longa de senha de teste')
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
  await page.route(documentUrl, async (route) => {
    const response = await route.fetch()
    await route.fulfill({
      response,
      headers: {
        ...response.headers(),
        'Content-Security-Policy': selfiePolicy.replace('{hostport}', new URL(baseURL).host),
        'Permissions-Policy': selfiePermissions,
      },
    })
  })
  await page.addInitScript(() => {
    const violations: string[] = []
    document.addEventListener('securitypolicyviolation', (event) => {
      violations.push(`${event.effectiveDirective} ${event.blockedURI}`)
      document.documentElement.dataset.selfieCspViolations = violations.join('\n')
    })
  })
  await page.goto('/login')
  await page.getByLabel('E-mail', { exact: true }).fill('csp@example.com')
  await page.getByLabel('Senha', { exact: true }).fill('frase longa de senha de teste')
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByText('csp@example.com')).toBeVisible()

  await page.goto(documentUrl)
  await page.getByRole('button', { name: 'Abrir câmera' }).click()
  await expect(page.locator('.liveness-detector')).toBeVisible()

  await expect.poll(() => sockets, { timeout: 15_000 }).toEqual(['streaming-rekognition.us-east-1.amazonaws.com'])
  expect(
    // Vídeo vivo da câmera sintética: a Permissions-Policy do documento liberou a câmera para a própria origem.
    await page.evaluate(() => {
      const video = document.querySelector('.liveness-detector video')
      return video instanceof HTMLVideoElement && video.srcObject instanceof MediaStream && video.srcObject.active
    }),
  ).toBe(true)
  expect(await page.evaluate(() => document.documentElement.dataset.selfieCspViolations)).toBeUndefined()
  expect(errors.filter((message) => /Content Security Policy|TrustedHTML|TrustedScript/i.test(message))).toEqual([])
})
