import { expect, test, type Page } from '@playwright/test'
import {
  authenticatedPage,
  expectSafeError,
  fulfillProblem,
  rejectPost,
  type Locale,
} from './support/problem-canary.ts'

const ID = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'

async function roomPage(page: Page, locale: Locale, scenario: { state: string; game: 'cards' | 'routes' | null }) {
  const { state, game } = scenario
  await authenticatedPage(page, locale)
  const room = {
    roomId: ID,
    state,
    version: '3',
    communicationLanguage: 'en',
    communicationPolicyVersion: 1,
    displayLocale: locale,
    invitationExpiresAt: '2099-01-01T00:00:00Z',
    audio: [],
    game:
      game === 'cards'
        ? {
            title: locale === 'pt-BR' ? 'Cartas sintéticas' : 'Synthetic cards',
            objective: locale === 'pt-BR' ? 'Escolha uma carta.' : 'Choose a card.',
            currentCard: {
              cardId: 'card-one',
              position: 0,
              prompt: locale === 'pt-BR' ? 'Qual caminho?' : 'Which path?',
              options: [{ optionId: 'one', label: locale === 'pt-BR' ? 'Caminho sintético' : 'Synthetic path' }],
            },
            awaitingPartner: false,
            revealed: [],
            passed: [],
            availableActions: ['choose-option', 'skip-card'],
          }
        : null,
    routesGame:
      game === 'routes'
        ? {
            experienceId: 'synthetic-routes',
            rulesVersion: 1,
            contentVersion: 1,
            schemaVersion: 1,
            resourceVersion: '3',
            status: 'Active',
            deadline: '2099-01-01T00:00:00Z',
            stage: 'Introduction',
            title: locale === 'pt-BR' ? 'Rotas sintéticas' : 'Synthetic routes',
            objective: locale === 'pt-BR' ? 'Escolha uma rota.' : 'Choose a route.',
            example: locale === 'pt-BR' ? 'Exemplo sintético.' : 'Synthetic example.',
            myClues: [],
            currentStop: null,
            isMyTurn: true,
            route: [],
            revisionsRemaining: 1,
            awaitingPartner: false,
            availableActions: ['acknowledge'],
          }
        : null,
  }
  await page.route(`**/api/v1/rooms/${ID}`, (route) => route.fulfill({ json: room }))
  await page.goto(`/rooms/${ID}`)
}

for (const locale of ['pt-BR', 'en-US'] as const) {
  test.describe(`mutation canaries ${locale}`, () => {
    test.use({ locale })
    const pt = locale === 'pt-BR'

    for (const failure of [
      { status: 400, code: 'MFA_CODE_INVALID' },
      { status: 401, code: 'REAUTHENTICATION_REQUIRED' },
      { status: 400, code: 'constructor' },
    ]) {
      test(`MFA confirmation ${failure.code} translates safely and retains the enrollment`, async ({ page }) => {
        await authenticatedPage(page, locale)
        await page.route('**/api/v1/me/mfa/enrollments', (route) =>
          route.fulfill({
            json: { sharedKey: 'SYNTHETICKEY', authenticatorUri: 'otpauth://totp/synthetic?secret=SYNTHETICKEY' },
          }),
        )
        await rejectPost(page, '/me/mfa/confirmations', failure)
        await page.goto('/account/enable-mfa')
        await page
          .getByRole('button', { name: pt ? 'Configurar autenticador' : 'Set up authenticator', exact: true })
          .click()
        const input = page.getByLabel(pt ? 'Código do autenticador' : 'Authenticator code', { exact: true })
        await input.fill('123456')
        await page.getByRole('button', { name: pt ? 'Ativar MFA' : 'Enable MFA', exact: true }).click()
        const message =
          failure.code === 'MFA_CODE_INVALID'
            ? pt
              ? 'Informe um código válido de 6 dígitos do autenticador.'
              : 'Enter a valid 6-digit authenticator code.'
            : failure.code === 'REAUTHENTICATION_REQUIRED'
              ? pt
                ? 'Entre novamente para confirmar sua identidade e continuar.'
                : 'Sign in again to confirm your identity and continue.'
              : undefined
        await expectSafeError(page, locale, message)
        await expect(input).toHaveValue('123456')
        await expect(page.getByText('SYNTHETICKEY', { exact: true })).toBeVisible()
      })
    }

    for (const step of ['start', 'capture'] as const) {
      test(`selfie ${step} failure never displays server text or requests a camera`, async ({ page }) => {
        await authenticatedPage(page, locale)
        await page.addInitScript(() => {
          Object.defineProperty(window, 'cameraRequests', { value: 0, writable: true })
          Object.defineProperty(MediaDevices.prototype, 'getUserMedia', {
            value: () => {
              const tracked = window as unknown as { cameraRequests: number }
              tracked.cameraRequests++
              return Promise.reject(new Error('Unexpected camera request'))
            },
          })
        })
        await page.route('**/api/v1/me/selfie-verifications/current', (route) =>
          route.fulfill({
            json: {
              id: step === 'start' ? null : ID,
              state: step === 'start' ? 'NotStarted' : 'Capturing',
              nextStep: step === 'start' ? 'Start' : 'Capture',
              feedbackCode: null,
              expiresAt: null,
            },
          }),
        )
        await rejectPost(
          page,
          step === 'start' ? '/me/selfie-verifications' : `/me/selfie-verifications/${ID}/capture`,
          { status: 503, code: 'SELFIE_UNAVAILABLE' },
        )
        await page.goto('/account/selfie')
        const button = page.getByRole('button', {
          name: step === 'start' ? (pt ? 'Iniciar captura' : 'Start capture') : pt ? 'Abrir câmera' : 'Open camera',
          exact: true,
        })
        await button.click()
        await expectSafeError(page, locale)
        await expect(button).toBeEnabled()
        expect(await page.evaluate(() => (window as unknown as { cameraRequests: number }).cameraRequests)).toBe(0)
      })
    }

    for (const command of ['accept', 'reject', 'exit', 'actions', 'routes-actions'] as const) {
      test(`room ${command} failure uses local fallback and keeps the room usable`, async ({ page }) => {
        const active = command !== 'accept' && command !== 'reject'
        await rejectPost(page, `/rooms/${ID}/${command}`, {
          status: command === 'actions' || command === 'routes-actions' ? 409 : 503,
          code: command === 'actions' || command === 'routes-actions' ? 'VERSION_CONFLICT' : 'TEMPORARILY_UNAVAILABLE',
        })
        await roomPage(page, locale, {
          state: active ? 'Active' : 'Invited',
          game: command === 'actions' ? 'cards' : command === 'routes-actions' ? 'routes' : null,
        })
        const label = {
          accept: pt ? 'Aceitar convite' : 'Accept invitation',
          reject: pt ? 'Recusar convite' : 'Decline invitation',
          exit: pt ? 'Sair da sala' : 'Leave room',
          actions: pt ? 'Caminho sintético' : 'Synthetic path',
          'routes-actions': pt ? 'Entendi' : 'Got it',
        }[command]
        const button = page.getByRole('button', { name: label, exact: true })
        await button.click()
        await expectSafeError(page, locale)
        await expect(button).toBeEnabled()
        await expect(page).toHaveURL(new RegExp(`/rooms/${ID}$`))
      })
    }

    for (const command of ['registration', 'check-in'] as const) {
      test(`event ${command} mutation ignores unavailable server text`, async ({ page }) => {
        await authenticatedPage(page, locale)
        await page.route('**/api/v1/events', (route) =>
          route.fulfill({
            json: [
              {
                eventId: ID,
                title: pt ? 'Evento sintético' : 'Synthetic event',
                startsAt: '2099-01-01T00:00:00Z',
                timeZone: 'UTC',
                checkInOpensAt: '2098-12-31T23:50:00Z',
                state: 'Published',
              },
            ],
          }),
        )
        await page.route(`**/api/v1/events/${ID}/participation`, (route) =>
          route.fulfill({ json: { state: 'Unregistered', roomId: null } }),
        )
        await page.route(`**/api/v1/events/${ID}/${command}`, async (route) => {
          expect(route.request().method()).toBe(command === 'registration' ? 'PUT' : 'POST')
          await fulfillProblem(route, { status: 503, code: 'TEMPORARILY_UNAVAILABLE' })
        })
        await page.goto('/events')
        const button = page.getByRole('button', {
          name:
            command === 'registration' ? (pt ? 'Inscrever-se' : 'Register') : pt ? 'Confirmar presença' : 'Check in',
          exact: true,
        })
        await button.click()
        await expectSafeError(page, locale)
        await expect(button).toBeEnabled()
      })
    }
  })
}
