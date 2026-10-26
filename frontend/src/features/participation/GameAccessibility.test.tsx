import { withSessionContext } from '@/test/private-context'
import { act, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server } from '@/test/msw'
import { renderApp } from '@/test/render'
import type { Locale } from '@/shared/i18n/locale'
import type { Room } from './participation-api'

const accountId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'
const deadline = '2099-10-24T18:00:00Z'
const first = {
  cardId: 'first',
  position: 1,
  prompt: 'First prompt',
  options: [{ optionId: 'one', label: 'One' }],
}
const second = {
  cardId: 'second',
  position: 2,
  prompt: 'Second prompt',
  options: [{ optionId: 'two', label: 'Two' }],
}
function room(kind: string, locale: Locale): Room {
  return {
    roomId: 'focus',
    state: 'Active',
    version: '4',
    communicationLanguage: 'en',
    communicationPolicyVersion: 1,
    displayLocale: locale,
    invitationExpiresAt: deadline,
    audio: [],
    game:
      kind === 'constellation'
        ? {
            title: 'Choices',
            objective: 'Choose together',
            deadline,
            currentCard: first,
            awaitingPartner: false,
            revealed: [],
            passed: [],
            availableActions: ['choose-option', 'skip-card'],
          }
        : null,
    routesGame:
      kind === 'routes'
        ? {
            experienceId: 'unlikely-routes',
            rulesVersion: 1,
            contentVersion: 1,
            schemaVersion: 1,
            resourceVersion: '4',
            status: 'Active',
            deadline,
            stage: 'Choices',
            title: 'Routes',
            objective: 'Plan together',
            example: 'An island',
            myClues: ['Your private clue'],
            currentStop: {
              stepId: 'first',
              prompt: 'First prompt',
              options: [{ optionId: 'one', label: 'One' }],
            },
            isMyTurn: true,
            route: [],
            revisionsRemaining: 1,
            awaitingPartner: false,
            availableActions: ['choose-option'],
          }
        : null,
  }
}
function advance(current: Room): Room {
  return {
    ...current,
    version: '5',
    game: current.game === null ? null : { ...current.game, currentCard: second, awaitingPartner: false },
    routesGame:
      current.routesGame === null
        ? null
        : {
            ...current.routesGame,
            resourceVersion: '5',
            currentStop: {
              stepId: 'second',
              prompt: 'Second prompt',
              options: [{ optionId: 'two', label: 'Two' }],
            },
          },
  }
}
function setup(initial: Room) {
  const snapshot = { value: initial }
  server.use(
    http.get(`${API}/me`, () =>
      HttpResponse.json(
        withSessionContext({
          id: accountId,
          email: 'test@example.com',
          status: 'Active',
          emailConfirmed: true,
          mfaEnabled: false,
          roles: ['participant'],
        }),
      ),
    ),
    http.get(`${API}/rooms/focus`, () => HttpResponse.json(snapshot.value)),
  )
  return snapshot
}
function liveContaining(text: string): Element | undefined {
  return [...document.querySelectorAll('[role="status"],[aria-live="polite"]')].find((e) =>
    e.textContent.includes(text),
  )
}
for (const locale of ['pt-BR', 'en-US'] as const) {
  describe.each(['constellation', 'routes'])('%s accessible updates ' + locale, (kind) => {
    it('returns focus to the next prompt only after the focused choice is removed', async () => {
      const snapshot = setup(room(kind, locale))
      const { queryClient, user } = await renderApp('/rooms/focus', undefined, locale)
      await screen.findByText('First prompt', { selector: 'legend' })
      const firstControl =
        kind === 'constellation'
          ? screen.getByRole('button', { name: 'One' })
          : screen.getByRole('radio', { name: 'One' })
      firstControl.focus()
      if (kind === 'routes') {
        await user.keyboard(' ')
        screen
          .getByRole('button', {
            name: locale === 'pt-BR' ? 'Escolher' : 'Choose',
          })
          .focus()
      }
      snapshot.value = advance(snapshot.value)
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: ['account', accountId, 'room', 'focus'],
        })
      })
      expect(await screen.findByText('Second prompt', { selector: 'legend' })).toHaveFocus()
    })
    it('preserves focus outside the game while a partner changes the prompt', async () => {
      const snapshot = setup(room(kind, locale))
      const { queryClient } = await renderApp('/rooms/focus', undefined, locale)
      await screen.findByText('First prompt', { selector: 'legend' })
      const exit = screen.getByRole('button', {
        name: locale === 'pt-BR' ? 'Sair da sala' : 'Leave room',
      })
      exit.focus()
      snapshot.value = advance(snapshot.value)
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: ['account', accountId, 'room', 'focus'],
        })
      })
      expect(exit).toHaveFocus()
    })
    it('does not reclaim focus after an explicit click outside the game', async () => {
      const snapshot = setup(room(kind, locale))
      const { queryClient, user } = await renderApp('/rooms/focus', undefined, locale)
      await screen.findByText('First prompt', { selector: 'legend' })
      const control =
        kind === 'constellation'
          ? screen.getByRole('button', { name: 'One' })
          : screen.getByRole('radio', { name: 'One' })
      control.focus()
      await user.click(document.body)
      snapshot.value = advance(snapshot.value)
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: ['account', accountId, 'room', 'focus'],
        })
      })
      await screen.findByText('Second prompt', { selector: 'legend' })
      expect(document.body).toHaveFocus()
    })
    it('leaves the focused choice and draft intact on an unchanged poll', async () => {
      setup(room(kind, locale))
      const { queryClient, user } = await renderApp('/rooms/focus', undefined, locale)
      await screen.findByText('First prompt', { selector: 'legend' })
      const control =
        kind === 'constellation'
          ? screen.getByRole('button', { name: 'One' })
          : screen.getByRole('radio', { name: 'One' })
      control.focus()
      if (kind === 'routes') await user.keyboard(' ')
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: ['account', accountId, 'room', 'focus'],
        })
      })
      expect(control).toHaveFocus()
      if (kind === 'routes') expect(control).toBeChecked()
    })
  })
  it('keeps the server-version reset of a route draft ' + locale, async () => {
    const snapshot = setup(room('routes', locale))
    const { queryClient, user } = await renderApp('/rooms/focus', undefined, locale)
    const option = await screen.findByRole('radio', { name: 'One' })
    option.focus()
    await user.keyboard(' ')
    expect(option).toBeChecked()
    if (snapshot.value.routesGame === null) throw new Error('Missing synthetic route')
    snapshot.value = {
      ...snapshot.value,
      version: '5',
      routesGame: { ...snapshot.value.routesGame, resourceVersion: '5' },
    }
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ['account', accountId, 'room', 'focus'],
      })
    })
    await screen.findByText('First prompt', { selector: 'legend' })
    await waitFor(() => expect(screen.getByRole('radio', { name: 'One' })).not.toBeChecked())
    expect(screen.getByRole('radio', { name: 'One' })).not.toBeChecked()
    expect(
      screen.getByRole('button', {
        name: locale === 'pt-BR' ? 'Escolher' : 'Choose',
      }),
    ).toBeDisabled()
  })
  it('keeps the next route stop locked on the partner turn ' + locale, async () => {
    const snapshot = setup(room('routes', locale))
    const { queryClient } = await renderApp('/rooms/focus', undefined, locale)
    const option = await screen.findByRole('radio', { name: 'One' })
    option.focus()
    snapshot.value = advance(snapshot.value)
    if (snapshot.value.routesGame === null) throw new Error('Missing synthetic route')
    snapshot.value = {
      ...snapshot.value,
      routesGame: {
        ...snapshot.value.routesGame,
        isMyTurn: false,
        availableActions: [],
      },
    }
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ['account', accountId, 'room', 'focus'],
      })
    })
    await screen.findByText('Second prompt', { selector: 'legend' })
    expect(screen.getByRole('radio', { name: 'Two' })).toBeDisabled()
    expect(
      screen.getByRole('button', {
        name: locale === 'pt-BR' ? 'Escolher' : 'Choose',
      }),
    ).toBeDisabled()
  })
  it('announces a new constellation prompt once, without announcing unchanged polls ' + locale, async () => {
    const snapshot = setup(room('constellation', locale))
    if (snapshot.value.game) snapshot.value.game.awaitingPartner = true
    const { queryClient } = await renderApp('/rooms/focus', undefined, locale)
    await screen.findByText('First prompt', { selector: 'legend' })
    const mutations: string[] = []
    const observer = new MutationObserver((records) => {
      for (const r of records) {
        const el = r.target.nodeType === 3 ? r.target.parentElement : r.target
        const live = el instanceof Element ? el.closest('[role="status"],[aria-live="polite"]') : null
        if (live) mutations.push(live.textContent)
      }
    })
    observer.observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true,
    })
    snapshot.value = advance(snapshot.value)
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ['account', accountId, 'room', 'focus'],
      })
    })
    await screen.findByText('Second prompt', { selector: 'legend' })
    expect(liveContaining('Second prompt')).toBeDefined()
    expect(mutations.filter((text) => text === 'Second prompt')).toHaveLength(1)
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ['account', accountId, 'room', 'focus'],
      })
    })
    expect(mutations.filter((text) => text === 'Second prompt')).toHaveLength(1)
    observer.disconnect()
  })
}
