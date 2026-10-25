import { withSessionContext } from './support/private-context.ts'
import { readFileSync } from 'node:fs'
import { expect, test } from '@playwright/test'

const CASE = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const image = readFileSync(new URL('../src/shared/ui/illustrations/ticket.webp', import.meta.url))
const caddy = readFileSync(new URL('../../infra/caddy/Caddyfile', import.meta.url), 'utf8')
const policy = /Content-Security-Policy "([^"\n]+)"/.exec(caddy)?.[1]

test('private review pixels render under the unchanged Caddy CSP and clear on close', async ({ page, baseURL }) => {
  if (baseURL === undefined || policy === undefined) throw new Error('Missing local baseURL or repository CSP')
  const documentUrl = new URL(`/admin/photo-reviews/${CASE}`, baseURL).href
  let evidenceReads = 0
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const headers = { 'Cache-Control': 'no-store' }
    if (path === '/api/v1/me') {
      await route.fulfill({
        json: withSessionContext({
          id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
          email: 'reviewer@example.test',
          status: 'Active',
          emailConfirmed: true,
          mfaEnabled: true,
          roles: ['moderator'],
          preferredLocale: 'en-US',
          preferredTimeZone: 'UTC',
        }),
        headers,
      })
    } else if (path === '/api/v1/admin/photo-reviews/capabilities') {
      await route.fulfill({
        json: { assign: true, evidence: true, decide: true, appeal: true, recentMfaRequiredForDecision: true },
        headers,
      })
    } else if (path === `/api/v1/admin/photo-reviews/${CASE}`) {
      await route.fulfill({
        json: {
          caseId: CASE,
          state: 'AwaitingReview',
          eTag: '"case-v1"',
          awaitingReviewAt: '2026-10-25T10:00:00Z',
          reviewDeadline: '2026-10-28T10:00:00Z',
          policyVersion: 'synthetic-policy',
        },
        headers: { ...headers, ETag: '"case-v1"' },
      })
    } else if (path === `/api/v1/admin/photo-reviews/${CASE}/evidence/card`) {
      evidenceReads++
      await route.fulfill({ body: image, contentType: 'image/webp', headers })
    } else {
      await route.fulfill({ status: 404, json: { title: 'Unconfigured synthetic route', status: 404 }, headers })
    }
  })
  await page.route(documentUrl, async (route) => {
    const response = await route.fetch()
    await route.fulfill({
      response,
      headers: {
        ...response.headers(),
        'Content-Security-Policy': policy.replace('{hostport}', new URL(baseURL).host),
      },
    })
  })
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (event) => {
      document.documentElement.dataset.photoReviewCspViolation = event.effectiveDirective
    })
  })
  await page.goto(documentUrl)
  const open = page.getByRole('button', { name: 'View private image' })
  await expect(open).toBeVisible()
  expect(evidenceReads).toBe(0)

  // Real-browser negative control: the old img/blob path must be rejected by the exact policy.
  const blocked = await page.evaluate(
    async (bytes) =>
      new Promise<boolean>((resolve) => {
        const legacy = new Image()
        const url = URL.createObjectURL(new Blob([Uint8Array.from(bytes)], { type: 'image/webp' }))
        const finish = (result: boolean): void => {
          legacy.remove()
          URL.revokeObjectURL(url)
          resolve(result)
        }
        legacy.onload = () => {
          finish(false)
        }
        legacy.onerror = () => {
          finish(true)
        }
        legacy.src = url
        document.body.append(legacy)
      }),
    Array.from(image),
  )
  expect(blocked).toBe(true)
  await expect.poll(() => page.evaluate(() => document.documentElement.dataset.photoReviewCspViolation)).toBe('img-src')
  await page.evaluate(() => {
    delete document.documentElement.dataset.photoReviewCspViolation
  })

  await open.click()
  // Keep addressing the same drawing surface after it leaves the accessibility tree on close.
  const canvas = page.locator('canvas[role="img"]')
  await expect(canvas).toBeVisible()
  await expect(canvas).toHaveAccessibleName('Private image for the assigned photo review')
  expect(
    await canvas.evaluate((element) => {
      if (!(element instanceof HTMLCanvasElement)) return false
      const context = element.getContext('2d')
      if (context === null || element.width === 0 || element.height === 0) return false
      return context
        .getImageData(0, 0, element.width, element.height)
        .data.some((value, index) => index % 4 === 3 && value !== 0)
    }),
  ).toBe(true)
  expect(evidenceReads).toBe(1)
  await expect(page.locator('img[src^="blob:"], img[src^="data:"]')).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.dataset.photoReviewCspViolation)).toBeUndefined()
  await page.getByRole('button', { name: 'Close image' }).click()
  await expect(canvas).toBeHidden()
  await expect(canvas).toHaveAttribute('width', '0')
  await expect(canvas).toHaveAttribute('height', '0')
})
