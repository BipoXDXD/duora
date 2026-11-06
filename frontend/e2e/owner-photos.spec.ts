import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']
const PASSWORD = 'frase longa de senha de teste'
const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const PHOTO_TAG = '"photo-bbbbbbbb-1"'
const PHOTO_BYTES = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x01, 0x02])

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function signIn(page: Page): Promise<void> {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser('ana@exemplo.com', PASSWORD)
  await page.goto('/login')
  await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
  await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page).not.toHaveURL(/\/login$/)
}

function json(route: Route, status: number, body: object): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

interface ReceivedUpload {
  readonly contentType: string | undefined
  readonly key: string | undefined
  readonly proof: string | undefined
  readonly bytes: Buffer | null
}

/** Backend mínimo de API-M01 a M03, com o gate do upload aberto. */
async function useOpenPhotoApi(
  page: Page,
): Promise<{ readonly uploads: ReceivedUpload[]; readonly ifMatch: string[] }> {
  const uploads: ReceivedUpload[] = []
  const ifMatch: string[] = []
  let items: object[] = []
  await page.route('**/api/v1/me/photos**', async (route) => {
    const request = route.request()
    const { pathname } = new URL(request.url())
    if (request.method() === 'POST') {
      const headers = await request.allHeaders()
      uploads.push({
        contentType: headers['content-type'],
        key: headers['idempotency-key'],
        proof: headers['x-xsrf-token'],
        bytes: request.postDataBuffer(),
      })
      items = [{ photoId: PHOTO, status: 'AwaitingReview', rejectionCode: null, createdAt: '2026-11-06T10:00:00Z' }]
      await json(route, 202, { photoId: PHOTO, status: 'Quarantined' })
    } else if (request.method() === 'DELETE') {
      ifMatch.push((await request.allHeaders())['if-match'] ?? '')
      items = []
      await route.fulfill({ status: 204, headers: { ETag: '"photo-bbbbbbbb-2"' } })
    } else if (pathname.endsWith('/status')) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { ETag: PHOTO_TAG },
        body: JSON.stringify({ photoId: PHOTO, status: 'AwaitingReview', rejectionCode: null }),
      })
    } else {
      await json(route, 200, { items, nextCursor: null })
    }
  })
  return { uploads, ifMatch }
}

test.describe('fotos do titular (#173)', () => {
  test('com o upload desligado no servidor, não há envio nem exclusão', async ({ page }) => {
    await signIn(page)
    await page.route('**/api/v1/me/photos**', (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'about:blank', title: 'x', status: 503, code: 'MEDIA_UPLOADS_DISABLED' }),
      }),
    )

    await page.goto('/account/photos')

    await expect(page.getByText('As fotos ainda não estão disponíveis no Duora.')).toBeVisible()
    await expect(page.locator('input[type="file"]')).toHaveCount(0)
    await expectNoAxeViolations(page)
  })

  test('envia o corpo cru, acompanha o estado e exclui na versão confirmada', async ({ page }) => {
    await signIn(page)
    const { uploads, ifMatch } = await useOpenPhotoApi(page)

    await page.goto('/account/photos')
    await page
      .getByLabel('Foto', { exact: true })
      .setInputFiles({ name: 'sintetica.jpg', mimeType: 'image/jpeg', buffer: PHOTO_BYTES })
    await expectNoAxeViolations(page)
    await page.getByRole('button', { name: 'Enviar foto' }).click()

    await expect(page.getByText('Foto recebida e enviada para análise.')).toBeVisible()
    await expect(page.getByText('Aguardando revisão')).toBeVisible()
    expect(uploads).toHaveLength(1)
    expect(uploads[0]?.contentType).toBe('image/jpeg')
    expect(uploads[0]?.key).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
    expect(uploads[0]?.proof).toBeTruthy()
    expect(uploads[0]?.bytes?.equals(PHOTO_BYTES)).toBe(true)

    await page.getByRole('button', { name: `Excluir foto ${PHOTO}` }).click()
    await expect(page.getByText('Estado atual: Aguardando revisão')).toBeVisible()
    await expectNoAxeViolations(page)
    await page.getByRole('button', { name: 'Confirmar exclusão' }).click()

    await expect(page.getByText('Foto excluída. A remoção dos arquivos termina em seguida.')).toBeVisible()
    await expect(page.getByText('Nenhuma foto disponível nesta lista.')).toBeVisible()
    expect(ifMatch).toEqual([PHOTO_TAG])
    await expectNoAxeViolations(page)
  })
})
