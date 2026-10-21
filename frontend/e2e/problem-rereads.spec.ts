import { expect, test, type Page } from '@playwright/test'
import {
  authenticatedPage,
  editableResource,
  expectDeliveredProblems,
  expectSafeDocument,
  GENERIC,
  type Locale,
  type ProblemReceipt,
} from './support/problem-canary.ts'

async function settings(page: Page, locale: Locale) {
  // No request may escape to a backend when a route is misspelled or missing.
  await page.route('**/api/**', (route) => route.abort())
  await authenticatedPage(page, locale)
  const resources = {
    preferences: await editableResource(page, '/me/preferences', {
      communicationLanguages: ['en'],
      allowDifferentDisplayLocales: false,
      declarationRequired: false,
      version: '1',
    }),
    locale: await editableResource(page, '/me/locale', { locale, version: '1' }),
    zone: await editableResource(page, '/me/time-zone', { timeZone: 'UTC', version: '1' }),
    regional: await editableResource(page, '/me/regional-preferences', {
      mode: 'pending',
      countryCodes: [] as string[],
      version: '1',
    }),
  }
  await page.route('**/api/v1/me/time-zone/options', (route) =>
    route.fulfill({ json: { timeZones: ['UTC', 'America/New_York', 'America/Sao_Paulo'] } }),
  )
  const writes: { path: string; version: string | undefined; body: unknown }[] = []
  const reads: string[] = []
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname
    if (request.method() === 'GET') reads.push(path)
    if (request.method() === 'PUT') {
      writes.push({ path, version: request.headers()['if-match'], body: request.postDataJSON() })
    }
  })
  await page.goto('/account/preferences')
  await page.waitForLoadState('networkidle')
  return { resources, writes, reads }
}

async function stableDocument(page: Page, locale: Locale) {
  // Move off the clicked button and observe the end of CSS transitions before measuring contrast.
  await page.mouse.move(0, 0)
  await expect.poll(() => page.evaluate(() => document.getAnimations().length)).toBe(0)
  await expectSafeDocument(page, locale)
}

async function errorStage(
  page: Page,
  locale: Locale,
  stage: { messages: readonly string[]; receipts: readonly ProblemReceipt[] },
) {
  // Locale has two identical generic alerts; count and ordered text assertions cover both.
  await expect(page.getByRole('alert')).toHaveCount(stage.messages.length)
  await expect(page.getByRole('alert')).toContainText([...stage.messages])
  await expectDeliveredProblems(page, stage.receipts)
  await stableDocument(page, locale)
}

async function settleTimers(page: Page): Promise<void> {
  await page.clock.runFor(10_000)
  // Flush timer-triggered requests, including a possible asynchronous CSRF refresh before a PUT.
  await page.evaluate(async () => {
    await fetch('/api/v1/me')
  })
  await page.waitForLoadState('networkidle')
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`failed conflict rereads ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'
    const labels = {
      portuguese: pt ? 'Português' : 'Portuguese',
      optIn: pt
        ? 'Aceito pares com idioma de exibição diferente do meu, desde que tenhamos um idioma de comunicação em comum.'
        : 'I accept matches with a different display language when we share a communication language.',
      locale: pt ? 'Idioma da interface e conteúdo' : 'Interface and content language',
      zone: pt ? 'Fuso horário da conta (opcional)' : 'Account time zone (optional)',
      regional: pt ? 'Preferência regional' : 'Regional preference',
      brazil: pt ? 'Brasil' : 'Brazil',
    }
    const cases = [
      {
        target: 'preferences',
        path: '/api/v1/me/preferences',
        save: pt ? 'Salvar preferências' : 'Save preferences',
        review: pt ? 'Revisar versão atual' : 'Review current version',
        conflict: pt
          ? 'Outra aba alterou estas preferências. Revise a versão atual antes de salvar seu rascunho.'
          : 'Another tab changed these preferences. Review the current version before saving your draft.',
        saved: pt ? 'Preferências salvas.' : 'Preferences saved.',
        body: { communicationLanguages: ['en', 'pt'], allowDifferentDisplayLocales: true },
      },
      {
        target: 'locale',
        path: '/api/v1/me/locale',
        save: pt ? 'Salvar idioma de exibição' : 'Save display language',
        review: pt ? 'Revisar versão atual' : 'Review current version',
        conflict: GENERIC[locale],
        saved: pt ? 'Display language saved.' : 'Idioma de exibição salvo.',
        body: { locale: pt ? 'en-US' : 'pt-BR' },
      },
      {
        target: 'zone',
        path: '/api/v1/me/time-zone',
        save: pt ? 'Salvar fuso horário' : 'Save time zone',
        review: pt ? 'Recarregar fuso atual' : 'Reload current time zone',
        conflict: pt
          ? 'Outra aba alterou seu fuso. Recarregue a versão atual; sua escolha será preservada para revisão.'
          : 'Another tab changed your time zone. Reload the current version; your choice will be kept for review.',
        saved: pt ? 'Fuso horário salvo.' : 'Time zone saved.',
        body: { timeZone: 'America/Sao_Paulo' },
      },
      {
        target: 'regional',
        path: '/api/v1/me/regional-preferences',
        save: pt ? 'Salvar preferência regional' : 'Save regional preference',
        review: pt ? 'Recarregar preferência regional' : 'Reload regional preference',
        conflict: pt
          ? 'A preferência regional mudou em outra aba. Seu rascunho foi preservado. Recarregue a versão atual antes de salvar novamente.'
          : 'Your regional preference changed in another tab. Your draft was preserved. Reload the current version before saving again.',
        saved: pt ? 'Preferência regional salva.' : 'Regional preference saved.',
        body: { mode: 'countries', countryCodes: ['BR'] },
      },
    ] as const

    for (const scenario of cases) {
      test(`${scenario.target} 412 then failed review retains state until explicit recovery and save`, async ({
        page,
      }) => {
        await page.clock.install()
        const { resources, writes, reads } = await settings(page, locale)
        const resource = resources[scenario.target]
        const save = page.getByRole('button', { name: scenario.save, exact: true })
        const review = page.getByRole('button', { name: scenario.review, exact: true })
        const otherLocale = pt ? 'en-US' : 'pt-BR'
        if (scenario.target === 'preferences') {
          await page.getByRole('checkbox', { name: labels.portuguese, exact: true }).check()
          await page.getByRole('checkbox', { name: labels.optIn, exact: true }).check()
        }
        if (scenario.target === 'locale')
          await page.getByLabel(labels.locale, { exact: true }).selectOption(otherLocale)
        if (scenario.target === 'zone') await page.getByLabel(labels.zone, { exact: true }).fill('America/Sao_Paulo')
        if (scenario.target === 'regional') {
          await page.getByLabel(labels.regional, { exact: true }).selectOption('countries')
          await page.getByRole('checkbox', { name: labels.brazil, exact: true }).check()
        }
        async function expectDraft() {
          if (scenario.target === 'preferences') {
            await expect(page.getByRole('checkbox', { name: labels.portuguese, exact: true })).toBeChecked()
            await expect(page.getByRole('checkbox', { name: labels.optIn, exact: true })).toBeChecked()
          }
          if (scenario.target === 'locale')
            await expect(page.getByLabel(labels.locale, { exact: true })).toHaveValue(otherLocale)
          if (scenario.target === 'zone')
            await expect(page.getByLabel(labels.zone, { exact: true })).toHaveValue('America/Sao_Paulo')
          if (scenario.target === 'regional') {
            await expect(page.getByLabel(labels.regional, { exact: true })).toHaveValue('countries')
            await expect(page.getByRole('checkbox', { name: labels.brazil, exact: true })).toBeChecked()
          }
        }
        const receipts: ProblemReceipt[] = [
          { method: 'PUT', path: scenario.path, status: 412, code: 'PRECONDITION_FAILED' },
        ]
        resource.failure = { status: 412, code: 'PRECONDITION_FAILED' }
        await save.click()
        await errorStage(page, locale, { messages: [scenario.conflict], receipts })
        expect(writes).toEqual([{ path: scenario.path, version: '"1"', body: scenario.body }])
        const initialReads = reads.filter((path) => path === scenario.path).length

        resource.readFailure = { status: 503, code: 'TEMPORARILY_UNAVAILABLE' }
        await review.click()
        receipts.push({ method: 'GET', path: scenario.path, status: 503, code: 'TEMPORARILY_UNAVAILABLE' })
        await errorStage(page, locale, { messages: [scenario.conflict, GENERIC[locale]], receipts })
        await expectDraft()
        await settleTimers(page)
        expect(writes).toHaveLength(1)
        expect(reads.filter((path) => path === scenario.path)).toHaveLength(initialReads + 1)
        if (scenario.target === 'locale') {
          // This form intentionally allows another explicit PUT without a successful review.
          await expect(save).toBeEnabled()
          await save.click()
          receipts.push({ method: 'PUT', path: scenario.path, status: 412, code: 'PRECONDITION_FAILED' })
          await errorStage(page, locale, { messages: [GENERIC[locale], GENERIC[locale]], receipts })
          expect(writes[1]).toEqual({ path: scenario.path, version: '"1"', body: scenario.body })
          await expectDraft()
        } else {
          await expect(save).toBeDisabled()
        }
        const failedWrites = scenario.target === 'locale' ? 2 : 1

        if (scenario.target === 'preferences')
          resources.preferences.value = {
            ...resources.preferences.value,
            communicationLanguages: ['pt'],
            version: '7',
          }
        if (scenario.target === 'locale') resources.locale.value = { locale, version: '7' }
        if (scenario.target === 'zone') resources.zone.value = { timeZone: 'America/New_York', version: '7' }
        if (scenario.target === 'regional')
          resources.regional.value = { mode: 'unrestricted', countryCodes: [], version: '7' }
        resource.readFailure = null
        await review.click()
        await expect(page.getByRole('alert')).toHaveCount(0)
        await expect(save).toBeEnabled()
        if (scenario.target === 'locale') {
          await expect(page.getByLabel(labels.locale, { exact: true })).toHaveValue(locale)
        } else {
          await expectDraft()
          const reloaded = {
            preferences: pt
              ? 'Versão atual carregada. Seu rascunho foi preservado; compare antes de salvar.'
              : 'Current version loaded. Your draft is preserved; compare before saving.',
            zone: pt
              ? 'Fuso atual: America/New_York. Sua escolha ainda não foi salva.'
              : 'Current time zone: America/New_York. Your choice has not been saved yet.',
            regional: pt
              ? 'Escolha salva atualmente: Sem restrição de país. Seu rascunho foi preservado; revise e salve para confirmar.'
              : 'Currently saved choice: No country restriction. Your draft was preserved; review and save to confirm.',
          }[scenario.target]
          const comparison = page.getByRole('status').filter({ hasText: reloaded })
          await expect(comparison).toBeVisible()
          if (scenario.target === 'preferences') {
            await expect(comparison).toContainText(pt ? 'Idiomas salvos: Português' : 'Saved languages: Portuguese')
            await expect(comparison).toContainText(
              pt
                ? 'Salvo: não aceita pares com outro idioma de exibição.'
                : 'Saved: does not accept matches with a different display language.',
            )
          }
        }
        await settleTimers(page)
        expect(writes).toHaveLength(failedWrites)
        expect(reads.filter((path) => path === scenario.path)).toHaveLength(initialReads + 2)
        await expect(page.locator('html')).toHaveAttribute('lang', locale)

        if (scenario.target === 'preferences')
          resources.preferences.value = {
            communicationLanguages: ['en', 'pt'],
            allowDifferentDisplayLocales: true,
            declarationRequired: false,
            version: '8',
          }
        if (scenario.target === 'locale') {
          await page.getByLabel(labels.locale, { exact: true }).selectOption(otherLocale)
          resources.locale.value = { locale: otherLocale, version: '8' }
        }
        if (scenario.target === 'zone') resources.zone.value = { timeZone: 'America/Sao_Paulo', version: '8' }
        if (scenario.target === 'regional')
          resources.regional.value = { mode: 'countries', countryCodes: ['BR'], version: '8' }
        resource.failure = null
        await save.click()
        await expect(page.getByRole('status').filter({ hasText: scenario.saved })).toBeVisible()
        expect(writes).toHaveLength(failedWrites + 1)
        expect(writes.at(-1)).toEqual({ path: scenario.path, version: '"7"', body: scenario.body })
        await expectDeliveredProblems(page, receipts)
        await stableDocument(page, scenario.target === 'locale' ? otherLocale : locale)
      })
    }
  })
}
