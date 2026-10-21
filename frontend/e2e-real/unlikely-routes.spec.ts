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
  routesGame: {
    stage: string
    myClues: string[]
    isMyTurn: boolean
    currentStop: { stepId: string; prompt: string; options: { optionId: string; label: string }[] } | null
    route: { stepId: string; optionLabel: string }[]
    revisionsRemaining: number
  }
  audio: { assetId: string; url: string; transcript: string }[]
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

test('real PostgreSQL Routes: bilingual private clues, alternating choices, revision and completion without microphone', async ({
  browser,
  request,
}) => {
  const seed = (await (await request.get('/__test/seed')).json()) as Seed
  const created = await request.post('/__test/routes-seed')
  expect(created.ok()).toBeTruthy()
  const { eventId } = (await created.json()) as { eventId: string }
  const contexts: BrowserContext[] = []
  let microphoneCalls = 0
  try {
    for (let i = 0; i < 2; i++) {
      const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'en-US', permissions: [] })
      contexts.push(context)
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
    }
    const alice = await at(contexts, 0).newPage()
    const bob = await at(contexts, 1).newPage()
    // Account locale may have been persisted by another real-browser scenario.
    for (const [index, page] of [alice, bob].entries()) {
      await page.goto('/entrar')
      await page.getByLabel('Email', { exact: true }).fill(at(seed.accounts, index).email)
      await page.getByLabel('Password', { exact: true }).fill(at(seed.accounts, index).password)
      await page.getByRole('button', { name: 'Sign in', exact: true }).click()
      await expect(page.getByRole('heading', { name: /Your account|Sua conta/ })).toBeVisible()
      await page.goto('/conta/preferencias')
      const locale = page.getByRole('combobox', {
        name: /Interface and content language|Idioma da interface e conteúdo/,
      })
      await locale.selectOption('en-US')
      await page.getByRole('button', { name: /Save display language|Salvar idioma de exibição/ }).click()
      await expect(page.getByText('Display language saved.', { exact: true })).toBeVisible()
      await declare(page, 'English', true)
    }
    await bob.goto('/conta/preferencias')
    await bob.getByRole('combobox', { name: 'Interface and content language' }).selectOption('pt-BR')
    await bob.getByRole('button', { name: 'Save display language' }).click()
    await expect(bob.getByText('Idioma de exibição salvo.')).toBeVisible()
    await Promise.all([enroll(alice, eventId), enroll(bob, eventId)])
    expect((await request.post('/__test/advance?seconds=301')).ok()).toBeTruthy()
    const id = await roomId(alice, eventId)
    const path = `/api/v1/rooms/${id}`
    const refresh = async () => {
      await Promise.all([alice.reload(), bob.reload()])
    }
    const clickAction = async (page: Page, label: string) => {
      const [result] = await Promise.all([
        page.waitForResponse(
          (response) => response.url().endsWith(`${path}/routes-actions`) && response.request().method() === 'POST',
        ),
        page.getByRole('button', { name: label, exact: true }).click(),
      ])
      expect(result.status()).toBe(204)
      await refresh()
    }
    await Promise.all([alice.goto(`/rooms/${id}`), bob.goto(`/rooms/${id}`)])
    await alice.getByRole('button', { name: 'Accept invitation' }).click()
    await bob.getByRole('button', { name: 'Aceitar convite' }).click()
    await expect.poll(async () => (await json<Room>(alice, path)).state).toBe('Active')
    await refresh()
    const intro = await json<Room>(alice, path)
    expect(intro.routesGame.myClues).toEqual([])
    expect(intro.audio[0]?.assetId).toBe('route-intro-en')
    const translated = await json<Room>(bob, path)
    expect(translated.audio[0]?.assetId).toBe('route-intro-pt')
    expect((await alice.request.get(at(intro.audio, 0).url)).status()).toBe(200)
    expect((await bob.request.get(at(intro.audio, 0).url)).status()).toBe(404)
    await expect(alice.locator('audio')).toHaveAttribute('preload', 'none')
    await clickAction(alice, 'Got it')
    await expect(alice.getByText('Waiting for the other person to confirm.')).toBeVisible()
    await clickAction(bob, 'Entendi')
    const left = await json<Room>(alice, path)
    const right = await json<Room>(bob, path)
    expect(left.routesGame.stage).toBe('Clues')
    expect(left.routesGame.myClues).toHaveLength(1)
    expect(right.routesGame.myClues).toHaveLength(1)
    const aliceHasPark = left.routesGame.myClues[0] === 'The park has flowers'
    expect(right.routesGame.myClues).toEqual([aliceHasPark ? 'O lago tem barcos' : 'O parque tem flores'])
    await expect(
      alice.getByText(aliceHasPark ? 'The lake has boats' : 'The park has flowers', { exact: true }),
    ).toHaveCount(0)
    await expect(
      bob.getByText(aliceHasPark ? 'O parque tem flores' : 'O lago tem barcos', { exact: true }),
    ).toHaveCount(0)
    await expect(alice.getByText(at(left.routesGame.myClues, 0), { exact: true })).toBeVisible()
    await expect(bob.getByText(at(right.routesGame.myClues, 0), { exact: true })).toBeVisible()
    await expect(alice.getByText(at(right.routesGame.myClues, 0), { exact: true })).toHaveCount(0)
    await clickAction(alice, 'Got it')
    await clickAction(bob, 'Entendi')
    const chooseRoute = async () => {
      const choosers: Page[] = []
      for (let index = 0; index < 2; index++) {
        const current = await json<Room>(alice, path)
        const page = current.routesGame.isMyTurn ? alice : bob
        const waiting = page === alice ? bob : alice
        choosers.push(page)
        await expect(waiting.getByRole('radio').first()).toBeDisabled()
        await page.getByRole('radio').first().check()
        await clickAction(page, page === alice ? 'Choose' : 'Escolher')
      }
      expect(choosers[0]).not.toBe(choosers[1])
      expect((await json<Room>(alice, path)).routesGame.stage).toBe('Adjustment')
    }
    await chooseRoute()
    const revisions = (await json<Room>(alice, path)).routesGame.revisionsRemaining
    await clickAction(bob, 'Pedir revisão')
    const revised = await json<Room>(alice, path)
    expect(revised.routesGame.route).toEqual([])
    expect(revised.routesGame.revisionsRemaining).toBe(revisions - 1)
    await chooseRoute()
    await clickAction(alice, 'Confirm route')
    expect((await json<Room>(bob, path)).state).toBe('Active')
    await clickAction(bob, 'Confirmar roteiro')
    await expect(alice.getByText('Experience completed.')).toBeVisible()
    await expect(bob.getByText('Experiência concluída.')).toBeVisible()
    const concluded = await json<Room>(alice, path)
    expect(concluded.routesGame.stage).toBe('Conclusion')
    expect(concluded.routesGame.route).toHaveLength(2)
    expect(microphoneCalls).toBe(0)
  } finally {
    await Promise.all(contexts.map((context) => context.close()))
  }
})
