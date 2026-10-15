import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { FakeDuoraApi } from './support/fake-api.ts'

// axe-core 4.13 marca critérios WCAG 2.2 A/AA com estas tags (ver smoke.spec.ts).
const WCAG_22_A_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

const PASSWORD = 'frase longa de senha de teste'

async function expectNoAxeViolations(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_22_A_AA_TAGS).analyze()
  expect(results.violations).toEqual([])
}

async function signIn(page: Page, email: string): Promise<void> {
  await page.getByLabel('E-mail', { exact: true }).fill(email)
  await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Entrar' }).click()
}

test.describe('jornada de entrada', () => {
  test('cadastro → verifique o e-mail → confirmar → entrar → conta → sair', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    await page.goto('/')

    await page.getByRole('main').getByRole('link', { name: 'Criar conta' }).click()
    await expect(page).toHaveTitle('Criar conta · Duora')
    await expect(page.getByRole('heading', { level: 1, name: 'Criar conta' })).toBeFocused()
    await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('checkbox', { name: /Termos de uso/ }).check()
    await page.getByRole('checkbox', { name: /Política de privacidade/ }).check()
    await page.getByRole('button', { name: 'Criar conta' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Verifique seu e-mail' })).toBeVisible()
    await expect(page).toHaveURL(/\/verifique-seu-email$/)

    // A pessoa abre o link recebido por e-mail.
    await page.goto(api.lastConfirmationLink().path)
    await page.getByRole('button', { name: 'Confirmar e-mail' }).click()
    await expect(page.getByRole('status')).toContainText('E-mail confirmado.')

    await page.getByRole('main').getByRole('link', { name: 'Entrar' }).click()
    await signIn(page, 'ana@exemplo.com')

    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
    await expect(page.getByText('ana@exemplo.com')).toBeVisible()
    await expect(page.getByText('Ativa', { exact: true })).toBeVisible()
    await expect(page.getByText('Esta sessão', { exact: true })).toBeVisible()

    await page.getByRole('button', { name: 'Sair' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Você saiu da sua conta.')

    await page.goto('/conta')
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Entre para continuar.')
  })

  test('T-21: A sai, B entra no mesmo navegador e nenhum dado de A aparece', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('ana@exemplo.com', PASSWORD)
    api.seedConfirmedUser('bruno@exemplo.com', PASSWORD)

    await page.goto('/entrar')
    await signIn(page, 'ana@exemplo.com')
    await expect(page.getByText('ana@exemplo.com')).toBeVisible()
    await expect(page.getByText('Navegador de teste (ana)')).toBeVisible()

    await page.getByRole('button', { name: 'Sair' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()

    // A partir daqui, qualquer aparição de dado de A no DOM fica registrada.
    await page.evaluate(() => {
      const leaks: string[] = []
      Reflect.set(window, '__vazamentosDeA', leaks)
      new MutationObserver(() => {
        const text = document.body.textContent
        if (text.includes('ana@exemplo.com') || text.includes('Navegador de teste (ana)')) {
          leaks.push(text)
        }
      }).observe(document.body, { subtree: true, childList: true, characterData: true })
    })

    await signIn(page, 'bruno@exemplo.com')
    await expect(page.getByText('bruno@exemplo.com')).toBeVisible()
    await expect(page.getByText('Navegador de teste (bruno)')).toBeVisible()

    await expect(page.getByText('ana@exemplo.com')).toHaveCount(0)
    await expect(page.getByText('Navegador de teste (ana)')).toHaveCount(0)
    expect(await page.evaluate((): unknown => Reflect.get(window, '__vazamentosDeA'))).toEqual([])
    // Nada privado persiste no navegador (spec 2.2 e 13.2).
    const storage = await page.evaluate(() =>
      [localStorage, sessionStorage]
        .flatMap((store) => Object.keys(store).map((key) => `${key}=${store.getItem(key) ?? ''}`))
        .join('\n'),
    )
    expect(storage).not.toContain('ana@exemplo.com')
    expect(storage).not.toContain('bruno@exemplo.com')
  })

  test('encerrar a sessão corrente leva à entrada', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('ana@exemplo.com', PASSWORD)
    await page.goto('/entrar')
    await signIn(page, 'ana@exemplo.com')

    await page.getByRole('button', { name: /^Encerrar esta sessão/ }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Entrar' })).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Você saiu da sua conta.')
  })

  test('entrada só com teclado: preencher e enviar com Enter', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('ana@exemplo.com', PASSWORD)
    await page.goto('/entrar')

    await page.getByLabel('E-mail', { exact: true }).focus()
    await page.keyboard.type('ana@exemplo.com')
    await page.keyboard.press('Tab')
    await expect(page.getByLabel('Senha', { exact: true })).toBeFocused()
    await page.keyboard.type(PASSWORD)
    await page.keyboard.press('Enter')

    await expect(page.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeVisible()
  })
})

test.describe('acessibilidade automática nos estados de erro (WCAG 2.2 A/AA)', () => {
  test('entrada com credenciais inválidas', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/entrar')
    await signIn(page, 'ninguem@exemplo.com')
    await expect(page.getByRole('alert')).toContainText('E-mail ou senha incorretos.')

    await expectNoAxeViolations(page)
  })

  test('entrada com limite temporário', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.failNext({ method: 'POST', path: '/auth/login' }, { status: 429, code: 'RATE_LIMITED', retryAfter: '120' })
    await page.goto('/entrar')
    await signIn(page, 'ana@exemplo.com')
    await expect(page.getByRole('alert')).toContainText('espere 2 minutos')

    await expectNoAxeViolations(page)
  })

  test('entrada com aviso de sessão necessária', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/conta')
    await expect(page.getByRole('status')).toContainText('Entre para continuar.')

    await expectNoAxeViolations(page)
  })

  test('cadastro com erros de validação', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/criar-conta')
    await page.getByLabel('Senha', { exact: true }).fill('curta')
    await page.getByRole('button', { name: 'Criar conta' }).click()
    await expect(page.getByLabel('E-mail', { exact: true })).toBeFocused()
    await expect(page.getByText('A senha precisa ter pelo menos 12 caracteres.')).toBeVisible()

    await expectNoAxeViolations(page)
  })

  test('cadastro com erro de rede', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.failNext({ method: 'POST', path: '/auth/register' }, 'network-error')
    await page.goto('/criar-conta')
    await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('checkbox', { name: /Termos de uso/ }).check()
    await page.getByRole('checkbox', { name: /Política de privacidade/ }).check()
    await page.getByRole('button', { name: 'Criar conta' }).click()
    await expect(page.getByRole('alert')).toContainText('Confira sua conexão')

    await expectNoAxeViolations(page)
  })

  test('verifique seu e-mail', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/verifique-seu-email')
    await expect(page.getByRole('heading', { level: 1, name: 'Verifique seu e-mail' })).toBeVisible()

    await expectNoAxeViolations(page)
  })

  test('confirmação com token inválido', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/confirmar-email#userId=0199f1a2-3b4c-7d5e-8f60-718293a4b5c6&token=vencido')
    await page.getByRole('button', { name: 'Confirmar e-mail' }).click()
    await expect(page.getByRole('alert')).toContainText('inválido, expirou ou já foi usado')

    await expectNoAxeViolations(page)
  })

  test('confirmação com link malformado', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/confirmar-email')
    await expect(page.getByRole('alert')).toBeVisible()

    await expectNoAxeViolations(page)
  })

  test('recuperação de senha com erro de rede', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.failNext({ method: 'POST', path: '/auth/password-reset-requests' }, 'network-error')
    await page.goto('/esqueci-a-senha')
    await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
    await page.getByRole('button', { name: 'Enviar link' }).click()
    await expect(page.getByRole('alert')).toContainText('Confira sua conexão')

    await expectNoAxeViolations(page)
  })

  test('recuperação de senha enviada', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/esqueci-a-senha')
    await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
    await page.getByRole('button', { name: 'Enviar link' }).click()
    await expect(page.getByRole('status')).toContainText('Se houver uma conta com esse e-mail')

    await expectNoAxeViolations(page)
  })

  test('redefinição com token inválido', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/redefinir-senha#userId=0199f1a2-3b4c-7d5e-8f60-718293a4b5c6&token=vencido')
    await page.getByLabel('Senha nova', { exact: true }).fill(PASSWORD)
    await page.getByRole('button', { name: 'Salvar senha nova' }).click()
    await expect(page.getByRole('alert')).toContainText('Peça um link novo.')

    await expectNoAxeViolations(page)
  })

  test('redefinição com erro de validação', async ({ page }) => {
    await FakeDuoraApi.install(page)
    await page.goto('/redefinir-senha#userId=0199f1a2-3b4c-7d5e-8f60-718293a4b5c6&token=abc')
    await page.getByRole('button', { name: 'Salvar senha nova' }).click()
    await expect(page.getByLabel('Senha nova', { exact: true })).toBeFocused()

    await expectNoAxeViolations(page)
  })

  test('conta com e-mail pendente e sessões fora do ar', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    await page.goto('/criar-conta')
    await page.getByLabel('E-mail', { exact: true }).fill('ana@exemplo.com')
    await page.getByLabel('Senha', { exact: true }).fill(PASSWORD)
    await page.getByRole('checkbox', { name: /Termos de uso/ }).check()
    await page.getByRole('checkbox', { name: /Política de privacidade/ }).check()
    await page.getByRole('button', { name: 'Criar conta' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Verifique seu e-mail' })).toBeVisible()
    api.failNext({ method: 'GET', path: '/me/sessions', times: 3 }, 'network-error')
    await page.goto('/entrar')
    await signIn(page, 'ana@exemplo.com')

    await expect(page.getByText('Confirme seu e-mail para continuar.')).toBeVisible()
    await expect(page.getByText('Não foi possível carregar as sessões.')).toBeVisible()

    await expectNoAxeViolations(page)
  })

  test('conta com a lista de sessões', async ({ page }) => {
    const api = await FakeDuoraApi.install(page)
    api.seedConfirmedUser('ana@exemplo.com', PASSWORD)
    await page.goto('/entrar')
    await signIn(page, 'ana@exemplo.com')
    await expect(page.getByText('Esta sessão', { exact: true })).toBeVisible()

    await expectNoAxeViolations(page)
  })
})
