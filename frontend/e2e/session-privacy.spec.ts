import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

/*
 * T-21 e a parte de navegador do T-14: depois de sair, de a sessão expirar ou de outra pessoa entrar no mesmo
 * navegador, nada de quem estava antes aparece (cache, formulário, histórico, storage, outra aba, bfcache).
 */

const PASSWORD = 'frase longa de senha de teste'
const ANA = 'ana@exemplo.com'
const BRUNO = 'bruno@exemplo.com'
const ANA_TRACES = [ANA, 'Navegador de teste (ana)']

/** Únicas chaves que o navegador pode guardar: preferências sem dado pessoal (idioma e guarda de recarga). */
const ALLOWED_STORAGE_KEYS = ['duora.locale', 'duora.newVersionReloadAt']

async function signIn(page: Page, email: string): Promise<void> {
  await page.getByLabel('E-mail', { exact: true }).fill(email)
  await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Entrar' }).click()
}

async function signInAsAna(page: Page): Promise<FakeDuoraApi> {
  const api = await FakeDuoraApi.install(page)
  api.seedConfirmedUser(ANA, PASSWORD)
  api.seedConfirmedUser(BRUNO, PASSWORD)
  await page.goto('/login')
  await signIn(page, ANA)
  await expect(page.getByText(ANA)).toBeVisible()
  await expect(page.getByText('Navegador de teste (ana)')).toBeVisible()
  return api
}

/**
 * A partir de agora, e neste documento, registra toda aparição de dado de Ana no DOM, inclusive por um
 * instante: um flash da tela anterior também é vazamento. Devolve a leitura do registro.
 */
async function watchForAnaTraces(page: Page): Promise<() => Promise<unknown>> {
  await page.evaluate((traces) => {
    const seen: string[] = []
    Reflect.set(window, '__dadosDeAna', seen)
    const record = (): void => {
      const text = document.body.textContent
      const current: unknown = Reflect.get(window, '__dadosDeAna')
      if (Array.isArray(current)) current.push(...traces.filter((trace) => text.includes(trace)))
    }
    record()
    new MutationObserver(record).observe(document.body, { subtree: true, childList: true, characterData: true })
  }, ANA_TRACES)
  return () => page.evaluate((): unknown => Reflect.get(window, '__dadosDeAna'))
}

async function expectNoAnaOnScreen(page: Page): Promise<void> {
  for (const trace of ANA_TRACES) {
    await expect(page.getByText(trace)).toHaveCount(0)
  }
}

/** Como o navegador ao voltar o foco para a aba: o TanStack Query reconsulta o que está em tela. */
async function refocus(page: Page): Promise<void> {
  await page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')))
}

test.describe('T-21: troca de identidade no mesmo navegador', () => {
  test('A sai, B entra e nenhum dado de A aparece, nem no formulário nem no storage', async ({ page }) => {
    await signInAsAna(page)

    await page.getByRole('button', { name: 'Sair' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByLabel('E-mail', { exact: true })).toHaveValue('')
    await expect(page.getByLabel('Senha', { exact: true })).toHaveValue('')
    const anaTraces = await watchForAnaTraces(page)

    await signIn(page, BRUNO)
    await expect(page.getByText(BRUNO)).toBeVisible()
    await expect(page.getByText('Navegador de teste (bruno)')).toBeVisible()

    await expectNoAnaOnScreen(page)
    expect(await anaTraces()).toEqual([])
    const storageKeys = await page.evaluate(() => [localStorage, sessionStorage].flatMap((store) => Object.keys(store)))
    expect(storageKeys.filter((key) => !ALLOWED_STORAGE_KEYS.includes(key))).toEqual([])
  })

  test('A sai em outra aba: dados somem na hora e a identidade exige confirmação explícita', async ({ page }) => {
    await signInAsAna(page)
    const otherTab = await page.context().newPage()
    await otherTab.goto('/account')
    await expect(otherTab.getByText(ANA)).toBeVisible()

    await otherTab.getByRole('button', { name: 'Sair' }).click()

    await expectNoAnaOnScreen(page)
    await page.getByRole('button', { name: 'Tentar de novo' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expectNoAnaOnScreen(page)
  })

  test('B entra em outra aba: a confirmação explícita instala B sem dados de A', async ({ page }) => {
    await signInAsAna(page)
    const otherTab = await page.context().newPage()
    await otherTab.goto('/login')
    const anaTraces = await watchForAnaTraces(page)
    // A entrada desta aba já começa limpa (o observador vê a tela de A antes da troca).
    await page.evaluate(() => Reflect.set(window, '__dadosDeAna', []))

    await signIn(otherTab, BRUNO)
    await expect(otherTab.getByText(BRUNO)).toBeVisible()

    await expectNoAnaOnScreen(page)
    await page.getByRole('button', { name: 'Tentar de novo' }).click()
    await expect(page.getByText(BRUNO)).toBeVisible()
    await expectNoAnaOnScreen(page)
    expect(await anaTraces()).toEqual([])
  })

  test('voltar no histórico até a conta depois de a sessão expirar não reexibe A', async ({ page }) => {
    const api = await signInAsAna(page)
    await page.getByRole('link', { name: 'Duora' }).click()
    await expect(page.getByRole('main').getByRole('link', { name: 'Entrar' })).toBeVisible()
    api.expireCurrentSession()
    const anaTraces = await watchForAnaTraces(page)

    await page.goBack()

    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Entre para continuar.')
    expect(await anaTraces()).toEqual([])
  })

  test('voltar no histórico até a conta sem rede não reexibe A', async ({ page }) => {
    const api = await signInAsAna(page)
    await page.getByRole('link', { name: 'Duora' }).click()
    await expect(page.getByRole('main').getByRole('link', { name: 'Entrar' })).toBeVisible()
    api.failNext({ method: 'GET', path: '/me', times: 3 }, 'network-error')
    const anaTraces = await watchForAnaTraces(page)

    await page.goBack()

    await expect(page.getByText('Não foi possível carregar sua conta.')).toBeVisible()
    expect(await anaTraces()).toEqual([])
  })

  test('página restaurada do bfcache esconde a tela e recarrega, conferindo a sessão no servidor', async ({ page }) => {
    // O Playwright desliga o bfcache dos navegadores; os eventos que ele dispararia são simulados no bundle real.
    const api = await signInAsAna(page)
    await page.evaluate(() => Reflect.set(window, '__mesmoDocumento', true))
    api.expireCurrentSession()

    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })))
    await expect(page.getByText(ANA)).toBeHidden()
    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })))

    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    expect(await page.evaluate((): unknown => Reflect.get(window, '__mesmoDocumento'))).toBeUndefined()
    await expectNoAnaOnScreen(page)
  })
})

test('página pública restaurada do bfcache não recarrega e mantém o rascunho do cadastro', async ({ page }) => {
  await FakeDuoraApi.install(page)
  await page.goto('/register')
  await page.getByLabel('E-mail', { exact: true }).fill(ANA)
  await page.evaluate(() => Reflect.set(window, '__mesmoDocumento', true))

  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })))
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })))

  await expect(page.getByLabel('E-mail', { exact: true })).toHaveValue(ANA)
  expect(await page.evaluate((): unknown => Reflect.get(window, '__mesmoDocumento'))).toBe(true)
})

test.describe('T-14: sessão expirada ou revogada no servidor', () => {
  test('a próxima leitura leva à entrada com o aviso, e quem entra depois não vê A', async ({ page }) => {
    const api = await signInAsAna(page)
    api.expireCurrentSession()

    await refocus(page)

    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Sua sessão expirou.')
    const anaTraces = await watchForAnaTraces(page)
    await signIn(page, BRUNO)
    await expect(page.getByText(BRUNO)).toBeVisible()
    expect(await anaTraces()).toEqual([])
  })

  test('a próxima escrita é negada e leva à entrada com o aviso', async ({ page }) => {
    const api = await signInAsAna(page)
    api.expireCurrentSession()

    await page.getByRole('button', { name: /^Encerrar esta sessão/ }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Sua sessão expirou.')
    await expectNoAnaOnScreen(page)
  })
})
