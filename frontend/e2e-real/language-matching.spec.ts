import { expect, test, type Page, type BrowserContext } from '@playwright/test'

interface Seed {
  events: string[]
  accounts: { userId: string; email: string; password: string }[]
}
interface Invitation {
  state: string
  roomId: string | null
}
interface Room {
  state: string
  version: string
  communicationLanguage: string
  displayLocale: string
  game: {
    currentCard: { cardId: string; prompt: string; options: { optionId: string; label: string }[] } | null
    contentVersion: number
    awaitingPartner: boolean
  } | null
}
function at<T>(values: T[], index: number): T {
  const value = values[index]
  if (value === undefined) throw new Error('Missing seeded fixture')
  return value
}
async function json<T>(page: Page, path: string): Promise<T> {
  const response = await page.request.get(path)
  expect(response.status()).toBe(200)
  return (await response.json()) as T
}
async function login(page: Page, account: Seed['accounts'][number]): Promise<void> {
  await page.goto('/entrar')
  await page.getByLabel('Email', { exact: true }).fill(account.email)
  await page.getByLabel('Password', { exact: true }).fill(account.password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Your account' })).toBeVisible()
}
async function declare(page: Page, language: 'English' | 'Portuguese', optIn: boolean): Promise<void> {
  await page.goto('/conta/preferencias')
  const english = page.getByRole('checkbox', { name: 'English', exact: true })
  const portuguese = page.getByRole('checkbox', { name: 'Portuguese', exact: true })
  await english.setChecked(language === 'English')
  await portuguese.setChecked(language === 'Portuguese')
  await page.getByRole('checkbox', { name: /I accept matches/ }).setChecked(optIn)
  await page.getByRole('button', { name: 'Save preferences', exact: true }).click()
  await expect(page.getByText('Preferences saved.', { exact: true })).toBeVisible()
}
async function enroll(page: Page, eventId: string): Promise<void> {
  // Read and write the real API with the same browser cookies/CSRF. No route interception or mocked responses.
  for (const [method, path] of [
    ['PUT', `/api/v1/events/${eventId}/registration`],
    ['POST', `/api/v1/events/${eventId}/check-in`],
  ] as const) {
    const result = await page.evaluate(
      async ({ method: verb, path: endpoint }) => {
        await fetch('/api/v1/auth/csrf')
        const token = document.cookie
          .split('; ')
          .find((cookie) => cookie.startsWith('XSRF-TOKEN='))
          ?.slice('XSRF-TOKEN='.length)
        const response = await fetch(endpoint, {
          method: verb,
          headers: { 'X-XSRF-TOKEN': decodeURIComponent(token ?? '') },
        })
        return response.status
      },
      { method, path },
    )
    expect(result).toBe(204)
  }
}
async function roomId(page: Page, eventId: string): Promise<string> {
  const status = await json<Invitation>(page, `/api/v1/events/${eventId}/participation`)
  expect(status.roomId).not.toBeNull()
  if (status.roomId === null) throw new Error('Missing invitation')
  return status.roomId
}

test('real PostgreSQL: no common language, either participant declines, then localized completion without microphone', async ({
  browser,
  request,
}) => {
  const response = await request.get('/__test/seed')
  const seed = (await response.json()) as Seed
  const contexts: BrowserContext[] = []
  let microphoneCalls = 0
  try {
    for (let i = 0; i < 3; i++)
      contexts.push(await browser.newContext({ ignoreHTTPSErrors: true, locale: 'en-US', permissions: [] }))
    const [alice, bob, third] = await Promise.all(
      contexts.map(async (context) => {
        await context.exposeBinding('recordMicrophoneCall', () => {
          microphoneCalls++
        })
        await context.addInitScript(() => {
          Object.defineProperty(navigator, 'mediaDevices', {
            configurable: true,
            value: {
              getUserMedia: () => {
                ;(window as unknown as { recordMicrophoneCall: () => void }).recordMicrophoneCall()
                throw new Error('Microphone must never be requested')
              },
            },
          })
        })
        return await context.newPage()
      }),
    )
    if (alice === undefined || bob === undefined || third === undefined) throw new Error('Missing browser context')
    await Promise.all([
      login(alice, at(seed.accounts, 0)),
      login(bob, at(seed.accounts, 1)),
      login(third, at(seed.accounts, 2)),
    ])
    await Promise.all([declare(alice, 'English', true), declare(bob, 'Portuguese', true)])
    await Promise.all([enroll(alice, at(seed.events, 0)), enroll(bob, at(seed.events, 0))])
    expect((await request.post('/__test/advance?seconds=301')).ok()).toBeTruthy()
    expect((await json<Invitation>(alice, `/api/v1/events/${at(seed.events, 0)}/participation`)).roomId).toBeNull()
    expect((await json<Invitation>(bob, `/api/v1/events/${at(seed.events, 0)}/participation`)).roomId).toBeNull()

    await declare(bob, 'English', true)
    for (let index = 1; index <= 2; index++) {
      const event = at(seed.events, index)
      await Promise.all([enroll(alice, event), enroll(bob, event)])
      expect((await request.post('/__test/advance?seconds=300')).ok()).toBeTruthy()
      const id = await roomId(alice, event)
      const rejecting = index === 1 ? alice : bob
      await rejecting.goto(`/rooms/${id}`)
      await rejecting.getByRole('button', { name: 'Decline invitation' }).click()
      await expect(rejecting.getByText('This encounter has ended.')).toBeVisible()
      const other = index === 1 ? bob : alice
      expect((await json<Room>(other, `/api/v1/rooms/${id}`)).state).toBe('Aborted')
    }

    // Persist a different display locale without changing Bob's explicitly declared English.
    await bob.goto('/conta/preferencias')
    await bob.getByRole('combobox', { name: 'Interface and content language' }).selectOption('pt-BR')
    await bob.getByRole('button', { name: 'Save display language' }).click()
    await expect(bob.getByText('Idioma de exibição salvo.')).toBeVisible()
    const last = at(seed.events, 3)
    await Promise.all([enroll(alice, last), enroll(bob, last)])
    expect((await request.post('/__test/advance?seconds=300')).ok()).toBeTruthy()
    const id = await roomId(alice, last)
    expect((await third.request.get(`/api/v1/rooms/${id}`)).status()).toBe(404)
    await Promise.all([alice.goto(`/rooms/${id}`), bob.goto(`/rooms/${id}`)])
    await expect(alice.getByText('Agreed language: English')).toBeVisible()
    await expect(bob.getByText('Idioma combinado: Inglês')).toBeVisible()
    await alice.getByRole('button', { name: 'Accept invitation' }).click()
    expect((await json<Room>(alice, `/api/v1/rooms/${id}`)).state).toBe('Invited')
    await Promise.all([
      bob.waitForResponse(
        (result) => result.url().endsWith(`/rooms/${id}/accept`) && result.request().method() === 'POST',
      ),
      bob.getByRole('button', { name: 'Aceitar convite' }).click(),
    ])
    await expect.poll(async () => (await json<Room>(alice, `/api/v1/rooms/${id}`)).state).toBe('Active')
    for (let card = 0; card < 6; card++) {
      const left = await json<Room>(alice, `/api/v1/rooms/${id}`)
      if (left.state === 'Completed') break
      const right = await json<Room>(bob, `/api/v1/rooms/${id}`)
      expect(left.game?.contentVersion).toBe(right.game?.contentVersion)
      expect(left.game?.currentCard?.cardId).toBe(right.game?.currentCard?.cardId)
      expect(left.game?.currentCard?.options.map((option) => option.optionId)).toEqual(
        right.game?.currentCard?.options.map((option) => option.optionId),
      )
      expect(left.game?.currentCard?.prompt).not.toBe(right.game?.currentCard?.prompt)
      const option = left.game?.currentCard?.options[0]?.optionId
      if (option === undefined) throw new Error('Missing card option')
      await Promise.all([
        alice.waitForResponse(
          (result) => result.url().endsWith(`/rooms/${id}/actions`) && result.request().method() === 'POST',
        ),
        alice.locator(`[data-option-id="${option}"]`).click(),
      ])
      await expect(bob.locator(`[data-option-id="${option}"]`)).toBeEnabled()
      // Refresh after the first commit so expectedVersion is current, exercising real optimistic concurrency.
      await bob.reload()
      await Promise.all([
        bob.waitForResponse(
          (result) => result.url().endsWith(`/rooms/${id}/actions`) && result.request().method() === 'POST',
        ),
        bob.locator(`[data-option-id="${option}"]`).click(),
      ])
      await alice.reload()
    }
    await expect(alice.getByText('Experience completed.')).toBeVisible()
    await expect(bob.getByText('Experiência concluída.')).toBeVisible()
    expect(microphoneCalls).toBe(0)
    const payload = await alice.request.get(`/api/v1/rooms/${id}`)
    expect(await payload.text()).not.toMatch(/communicationLanguages|allowDifferentDisplayLocales|privateState/)
  } finally {
    await Promise.all(
      contexts.map(async (context) => {
        await context.close()
      }),
    )
  }
})
