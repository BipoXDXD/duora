import { AxeBuilder } from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

const PASSWORD = 'synthetic long test passphrase'
const EMAIL = 'routes@example.com'
const publicAliases = [
  ['/criar-conta', '/register'],
  ['/cadastro', '/register'],
  ['/verifique-seu-email', '/check-email'],
  ['/entrar', '/login'],
  ['/confirmar-email', '/confirm-email'],
  ['/redefinir-senha', '/reset-password'],
  ['/esqueci-a-senha', '/forgot-password'],
  ['/recuperar-senha', '/forgot-password'],
] as const
const protectedAliases = [
  ['/conta', '/account'],
  ['/conta/perfil', '/account/profile'],
  ['/conta/preferencias', '/account/preferences'],
  ['/conta/selfie', '/account/selfie'],
  ['/conta/verificacao-etaria', '/account/selfie'],
  ['/conta/ativar-mfa', '/account/enable-mfa'],
  ['/trocar-senha-inicial', '/change-initial-password'],
] as const

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`route compatibility ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    const emailLabel = pt ? 'E-mail' : 'Email'
    const passwordLabel = pt ? 'Senha' : 'Password'
    const loginLabel = pt ? 'Entrar' : 'Sign in'

    test('public aliases and canonical URLs share accessible localized pages', async ({ page }) => {
      await FakeDuoraApi.install(page)
      for (const [legacy, canonical] of publicAliases) {
        for (const path of [legacy, canonical]) {
          await page.goto(path)
          await expect(page).toHaveURL(new RegExp(`${canonical}$`))
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
          await expect(page.locator('html')).toHaveAttribute('lang', locale)
        }
      }
      await page.goto('/entrar?source=email#help')
      await expect(page).toHaveURL(/\/login\?source=email#help$/)
      await page
        .getByRole('main')
        .getByRole('link', { name: pt ? 'Criar conta' : 'Create account', exact: true })
        .click()
      await expect(page.getByRole('heading', { level: 1, name: pt ? 'Criar conta' : 'Create account' })).toBeFocused()
      expect(
        (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()).violations,
      ).toEqual([])
    })

    test('all protected aliases still require a session and retain the canonical return', async ({ page }) => {
      await FakeDuoraApi.install(page)
      for (const [legacy, canonical] of protectedAliases) {
        for (const path of [legacy, canonical]) {
          await page.goto(`${path}?tab=details#section`)
          await expect(page).toHaveURL(/\/login$/)
          await expect(page.getByRole('heading', { level: 1, name: loginLabel })).toBeVisible()
          expect(await page.evaluate(() => (history.state as { usr: { from: string } }).usr.from)).toBe(
            `${canonical}?tab=details#section`,
          )
        }
      }
      await page.goto('/conta/perfil?TOKEN=synthetic-secret&userId=private&tab=photos#token=synthetic-secret')
      await expect(page).toHaveURL(/\/login$/)
      expect(await page.evaluate(() => history.state as unknown)).toMatchObject({
        usr: { from: '/account/profile?tab=photos' },
      })
      expect(await page.evaluate(() => JSON.stringify(history.state))).not.toContain('synthetic-secret')
    })

    test('legacy profile return survives login and replaces the legacy history entry', async ({ page }) => {
      const api = await FakeDuoraApi.install(page)
      api.seedConfirmedUser(EMAIL, PASSWORD)
      await page.goto('/')
      await page.goto('/conta/perfil?tab=photos#section')
      await expect(page).toHaveURL(/\/login$/)
      await page.getByLabel(emailLabel, { exact: true }).fill(EMAIL)
      await page.getByLabel(passwordLabel, { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: loginLabel, exact: true }).click()
      await expect(page).toHaveURL(/\/account\/profile\?tab=photos#section$/)
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
      await page.goBack()
      await expect(page).toHaveURL(/\/$/)
    })

    for (const legacy of [false, true]) {
      for (const code of [false, true]) {
        test(`confirmation ${legacy ? 'legacy path' : 'canonical path'} / ${code ? 'code' : 'legacy token'}`, async ({
          page,
        }) => {
          const api = await FakeDuoraApi.install(page)
          const token = code ? '01234567' : 'synthetic+token/=&value'
          api.seedPendingUser(EMAIL, PASSWORD, token)
          const link = api.lastEmailLink('confirmation').path
          const requestUrls: string[] = []
          page.on('request', (request) => requestUrls.push(request.url()))
          await page.goto('/')
          await page.goto(legacy ? link.replace('/confirm-email', '/confirmar-email') : link)
          await expect(page).toHaveURL(/\/confirm-email$/)
          await page.getByLabel(passwordLabel, { exact: true }).fill(PASSWORD)
          if (code)
            await page.getByLabel(pt ? 'Código de confirmação' : 'Confirmation code', { exact: true }).fill('0123 4567')
          const submission = page.waitForRequest(
            (request) => request.url().endsWith('/auth/confirm-email') && request.method() === 'POST',
          )
          await page.getByRole('button', { name: pt ? 'Confirmar e-mail' : 'Confirm email', exact: true }).click()
          expect((await submission).postDataJSON()).toMatchObject({ token, password: PASSWORD })
          await expect(page.getByRole('status')).toContainText(pt ? 'E-mail confirmado.' : 'Email confirmed.')
          expect(requestUrls.some((url) => /[?&#](token|userId)=/.test(url))).toBe(false)
          expect(
            await page.evaluate(() => JSON.stringify([history.state as unknown, localStorage, sessionStorage])),
          ).not.toContain(token)
          await page.goBack()
          await expect(page).toHaveURL(/\/$/)
          await page.goForward()
          await expect(page).toHaveURL(/\/confirm-email$/)
          await expect(page.getByRole('alert')).toBeVisible()
        })
      }

      test(`password reset ${legacy ? 'legacy' : 'canonical'} preserves token and permits login`, async ({ page }) => {
        const api = await FakeDuoraApi.install(page)
        api.seedConfirmedUser(EMAIL, PASSWORD)
        await page.goto(legacy ? '/recuperar-senha' : '/forgot-password')
        await page.getByLabel(emailLabel, { exact: true }).fill(EMAIL)
        await page.getByRole('button', { name: pt ? 'Enviar link' : 'Send link', exact: true }).click()
        await expect(page.getByRole('status')).toBeVisible()
        const link = api.lastEmailLink('password-reset')
        await page.goto(legacy ? link.path.replace('/reset-password', '/redefinir-senha') : link.path)
        await expect(page).toHaveURL(/\/reset-password$/)
        await page.getByLabel(pt ? 'Senha nova' : 'New password', { exact: true }).fill(`${PASSWORD} new`)
        const submission = page.waitForRequest(
          (request) => request.url().endsWith('/auth/password-resets') && request.method() === 'POST',
        )
        await page.getByRole('button', { name: pt ? 'Salvar senha nova' : 'Save new password', exact: true }).click()
        expect((await submission).postDataJSON()).toMatchObject({ userId: link.userId, token: link.token })
        await expect(page.getByRole('status')).toContainText(pt ? 'Senha alterada.' : 'Password changed.')
        await page.getByRole('main').getByRole('link', { name: loginLabel, exact: true }).click()
        await page.getByLabel(emailLabel, { exact: true }).fill(EMAIL)
        await page.getByLabel(passwordLabel, { exact: true }).fill(`${PASSWORD} new`)
        await page.getByRole('button', { name: loginLabel, exact: true }).click()
        await expect(page).toHaveURL(/\/account$/)
      })
    }

    test('external history return cannot redirect the authenticated user', async ({ page }) => {
      const api = await FakeDuoraApi.install(page)
      api.seedConfirmedUser(EMAIL, PASSWORD)
      await page.goto('/entrar?returnTo=https://evil.example')
      await expect(page).toHaveURL(/\/login\?returnTo=/)
      await page.evaluate(() => {
        history.replaceState(
          { ...(history.state as Record<string, unknown>), usr: { from: '/\t/evil.example/account' } },
          '',
        )
      })
      await page.reload()
      await page.getByLabel(emailLabel, { exact: true }).fill(EMAIL)
      await page.getByLabel(passwordLabel, { exact: true }).fill(PASSWORD)
      await page.getByRole('button', { name: loginLabel, exact: true }).click()
      await expect(page).toHaveURL(/\/account$/)
    })
  })
}
