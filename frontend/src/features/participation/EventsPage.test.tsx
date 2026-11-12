import { withSessionContext } from '@/test/private-context'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { API, server } from '@/test/msw'
import { renderApp } from '@/test/render'
import { canWithdrawRegistration } from './withdrawal'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Active',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}
const eventId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c7'
const roomId = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c8'
const ROUND_CANCELLED_PT =
  'A rodada foi cancelada. Sua presença continua confirmada. Se houver outra rodada, o convite aparece aqui.'
const ROUND_CANCELLED_EN =
  "This round was cancelled. You're still checked in. If there's another round, your invitation will appear here."

const WITHDRAW_CLOSED_PT = 'Não é mais possível cancelar a inscrição.'
const WITHDRAW_CLOSED_EN = 'You can no longer cancel your registration.'

interface ParticipationBody {
  readonly state: string
  readonly roomId: string | null
  readonly roundCancelled: boolean
}

/** Uma resposta por leitura: o teste troca o corpo e pede nova leitura, como o polling faria. */
function serveEventWithParticipation(read: () => ParticipationBody): void {
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/events`, () =>
      HttpResponse.json([
        {
          eventId,
          title: 'Evento sintético',
          startsAt: '2099-01-01T00:00:00Z',
          timeZone: 'UTC',
          checkInOpensAt: '2098-12-31T23:50:00Z',
          state: 'Published',
        },
      ]),
    ),
    http.get(`${API}/events/${eventId}/participation`, () => HttpResponse.json(read())),
  )
}

describe('events after an aborted room', () => {
  it('offers no room link and shows the waiting message, whatever ended the room', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/events`, () =>
        HttpResponse.json([
          {
            eventId,
            title: 'Evento sintético',
            startsAt: '2099-01-01T00:00:00Z',
            timeZone: 'UTC',
            checkInOpensAt: '2098-12-31T23:50:00Z',
            state: 'Published',
          },
        ]),
      ),
      http.get(`${API}/events/${eventId}/participation`, () => HttpResponse.json({ state: 'Waiting', roomId: null })),
    )
    await renderApp('/events')
    expect(await screen.findByText(/Aguardando um par compatível/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Abrir convite ou sala' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('events after a cancelled round', () => {
  it('replaces the waiting message with a neutral status notice', async () => {
    serveEventWithParticipation(() => ({ state: 'Waiting', roomId: null, roundCancelled: true }))
    await renderApp('/events')

    const notice = await screen.findByText(ROUND_CANCELLED_PT)
    expect(notice).toHaveAttribute('role', 'status')
    expect(screen.queryByText(/Aguardando um par compatível/)).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows the same notice in English', async () => {
    serveEventWithParticipation(() => ({ state: 'Waiting', roomId: null, roundCancelled: true }))
    await renderApp('/events', undefined, 'en-US')

    expect(await screen.findByText(ROUND_CANCELLED_EN)).toHaveAttribute('role', 'status')
    expect(screen.queryByText(/Waiting for a compatible match/)).not.toBeInTheDocument()
  })

  it('keeps the usual waiting message when no round was cancelled', async () => {
    serveEventWithParticipation(() => ({ state: 'Waiting', roomId: null, roundCancelled: false }))
    await renderApp('/events')

    expect(await screen.findByText(/Aguardando um par compatível/)).toBeInTheDocument()
    expect(screen.queryByText(ROUND_CANCELLED_PT)).not.toBeInTheDocument()
  })

  it('does not move focus when the notice appears on a later poll', async () => {
    let body: ParticipationBody = { state: 'Waiting', roomId: null, roundCancelled: false }
    serveEventWithParticipation(() => body)
    const { queryClient } = await renderApp('/events')
    await screen.findByText(/Aguardando um par compatível/)
    const withdraw = screen.getByRole('button', { name: 'Cancelar inscrição' })
    withdraw.focus()

    body = { state: 'Waiting', roomId: null, roundCancelled: true }
    await queryClient.refetchQueries()

    expect(await screen.findByText(ROUND_CANCELLED_PT)).toBeInTheDocument()
    expect(withdraw).toHaveFocus()
  })

  it('goes away when the person gets a room in a new round', async () => {
    let body: ParticipationBody = { state: 'Waiting', roomId: null, roundCancelled: true }
    serveEventWithParticipation(() => body)
    const { queryClient } = await renderApp('/events')
    await screen.findByText(ROUND_CANCELLED_PT)

    body = { state: 'Invited', roomId, roundCancelled: false }
    await queryClient.refetchQueries()

    expect(await screen.findByRole('link', { name: 'Abrir convite ou sala' })).toBeInTheDocument()
    expect(screen.queryByText(ROUND_CANCELLED_PT)).not.toBeInTheDocument()
  })

  it('goes away when a new round starts and the person is still waiting', async () => {
    let body: ParticipationBody = { state: 'Waiting', roomId: null, roundCancelled: true }
    serveEventWithParticipation(() => body)
    const { queryClient } = await renderApp('/events')
    await screen.findByText(ROUND_CANCELLED_PT)

    body = { state: 'Waiting', roomId: null, roundCancelled: false }
    await queryClient.refetchQueries()

    expect(await screen.findByText(/Aguardando um par compatível/)).toBeInTheDocument()
    expect(screen.queryByText(ROUND_CANCELLED_PT)).not.toBeInTheDocument()
  })
})

// Quem já fez check-in não tem mais o que fazer com "Inscrever-se" e "Confirmar presença" (#434).
describe('registration and check-in buttons', () => {
  it.each([
    ['Waiting', false],
    ['CheckedIn', false],
    ['Confirmed', true],
  ])('hides them when they no longer apply (%s)', async (state, round) => {
    serveEventWithParticipation(() => ({ state, roomId: null, roundCancelled: round }))
    await renderApp('/events')

    await screen.findByRole('button', { name: /Cancelar inscrição/ })
    expect(screen.queryByRole('button', { name: 'Inscrever-se' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar presença' })).not.toBeInTheDocument()
  })

  it('offers them before registration', async () => {
    serveEventWithParticipation(() => ({ state: 'Unregistered', roomId: null, roundCancelled: false }))
    await renderApp('/events')

    expect(await screen.findByRole('button', { name: 'Inscrever-se' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar presença' })).toBeInTheDocument()
  })

  it('shows a loading status for the event until its participation arrives', async () => {
    let release: () => void = () => undefined
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/events`, () =>
        HttpResponse.json([
          {
            eventId,
            title: 'Evento sintético',
            startsAt: '2099-01-01T00:00:00Z',
            timeZone: 'UTC',
            checkInOpensAt: '2098-12-31T23:50:00Z',
            state: 'Published',
          },
        ]),
      ),
      http.get(`${API}/events/${eventId}/participation`, async () => {
        await gate
        return HttpResponse.json({ state: 'Unregistered', roomId: null })
      }),
    )
    await renderApp('/events')

    expect(await screen.findByText('Carregando sua situação neste encontro…')).toHaveAttribute('role', 'status')
    expect(screen.queryByText(/Inscreva-se e confirme/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Inscrever-se' })).not.toBeInTheDocument()
    release()
    expect(await screen.findByRole('button', { name: 'Inscrever-se' })).toBeInTheDocument()
    expect(screen.queryByText('Carregando sua situação neste encontro…')).not.toBeInTheDocument()
  })
})

describe('events with a room from an earlier round', () => {
  // Comportamento atual, fixado de propósito: com sala na leitura, a tela mostra o link e não o aviso. Mostrar o aviso
  // também nesse caso é decisão de produto pendente (ressalva C da revisão do #434).
  it('keeps the room link and shows no cancellation notice even when the latest round was cancelled', async () => {
    serveEventWithParticipation(() => ({ state: 'Completed', roomId, roundCancelled: true }))
    await renderApp('/events')

    expect(await screen.findByRole('link', { name: 'Abrir convite ou sala' })).toBeInTheDocument()
    expect(screen.queryByText(ROUND_CANCELLED_PT)).not.toBeInTheDocument()
  })
})

function eventsWith(
  state: string,
  onDelete: () => Response | Promise<Response> = () => new HttpResponse(null, { status: 204 }),
) {
  let current = state
  const deletes: Request[] = []
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
    http.get(`${API}/events`, () =>
      HttpResponse.json([
        {
          eventId,
          title: 'Evento sintético',
          startsAt: '2099-01-01T00:00:00Z',
          timeZone: 'UTC',
          checkInOpensAt: '2098-12-31T23:50:00Z',
          state: 'Published',
        },
      ]),
    ),
    http.get(`${API}/events/${eventId}/participation`, () => HttpResponse.json({ state: current, roomId: null })),
    http.delete(`${API}/events/${eventId}/registration`, async ({ request }) => {
      deletes.push(request.clone())
      const response = await onDelete()
      if (response.status === 204) {
        current = 'Withdrawn'
      }
      return response
    }),
  )
  return deletes
}

describe('withdrawing from an event (API-E04)', () => {
  it.each([
    ['Confirmed', 'Cancelar inscrição'],
    ['Waiting', 'Cancelar inscrição'],
    ['Waitlisted', 'Sair da lista de espera'],
  ])('offers the withdrawal that fits %s', async (state, label) => {
    eventsWith(state)
    await renderApp('/events')
    expect(await screen.findByRole('button', { name: label })).toBeInTheDocument()
  })

  it('offers no withdrawal to someone who is not registered', async () => {
    eventsWith('Unregistered')
    await renderApp('/events')
    expect(await screen.findByRole('button', { name: 'Inscrever-se' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Cancelar inscrição|Sair da lista/ })).not.toBeInTheDocument()
  })

  it('asks for confirmation and warns that there is no way back before calling the server', async () => {
    const user = userEvent.setup()
    const deletes = eventsWith('Confirmed')
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: 'Cancelar inscrição' }))

    const warning = screen.getByText(/não poderá se inscrever de novo neste encontro/)
    expect(warning).toHaveFocus()
    expect(screen.getByRole('button', { name: 'Sim, cancelar inscrição' })).toHaveAccessibleDescription(
      warning.textContent,
    )
    expect(deletes).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Manter inscrição' }))

    expect(deletes).toHaveLength(0)
    expect(screen.queryByText(/não poderá se inscrever de novo/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancelar inscrição' })).toHaveFocus()
  })

  it('withdraws after confirming, with no body, and then hides registration and check-in', async () => {
    const user = userEvent.setup()
    const deletes = eventsWith('Waitlisted')
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: 'Sair da lista de espera' }))
    await user.click(screen.getByRole('button', { name: 'Sim, sair da lista' }))

    const outcome = await screen.findByText('Você saiu deste encontro.')
    await waitFor(() => {
      expect(outcome).toHaveFocus()
    })
    expect(deletes).toHaveLength(1)
    expect(await deletes[0]?.text()).toBe('')
    expect(screen.queryByRole('button', { name: 'Inscrever-se' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar presença' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Sair da lista/ })).not.toBeInTheDocument()
  })

  // A ação segura vem antes do destrutivo, como nos diálogos de saída (A11Y-03).
  it.each([
    ['Confirmed', 'Cancelar inscrição', ['Manter inscrição', 'Sim, cancelar inscrição']],
    ['Waitlisted', 'Sair da lista de espera', ['Continuar na lista', 'Sim, sair da lista']],
  ])('puts the safe choice first when confirming (%s)', async (state, trigger, expected) => {
    const user = userEvent.setup()
    eventsWith(state)
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: trigger }))

    const group = screen.getByRole('group', { name: trigger })
    expect(
      within(group)
        .getAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(expected)
  })

  it('confirms in English with the wording of each state', async () => {
    const user = userEvent.setup()
    eventsWith('Waitlisted')
    await renderApp('/events', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: 'Leave the waitlist' }))

    expect(screen.getByRole('button', { name: 'Stay on the waitlist' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Yes, leave the waitlist' })).toBeInTheDocument()
  })

  // O servidor recusa com 404 no início do encontro, com o evento fora de Published (cancelado) ou com a inscrição
  // em outro estado: a tela diz só que a retirada fechou, sem alerta genérico e sem afirmar um motivo.
  it('says neutrally that withdrawal is closed when the server refuses with 404', async () => {
    const user = userEvent.setup()
    eventsWith('Confirmed', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:not-found', title: 'Not Found', status: 404, traceId: 'trace' },
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: 'Cancelar inscrição' }))
    await user.click(screen.getByRole('button', { name: 'Sim, cancelar inscrição' }))

    const notice = await screen.findByText(WITHDRAW_CLOSED_PT)
    expect(notice).toHaveAttribute('role', 'status')
    await waitFor(() => {
      expect(notice).toHaveFocus()
    })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText('Você saiu deste encontro.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Cancelar inscrição/ })).not.toBeInTheDocument()
  })

  it('says neutrally in English that withdrawal is closed when the server refuses with 404', async () => {
    const user = userEvent.setup()
    eventsWith('Confirmed', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:not-found', title: 'Not Found', status: 404, traceId: 'trace' },
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    await renderApp('/events', undefined, 'en-US')
    await user.click(await screen.findByRole('button', { name: 'Cancel registration' }))
    await user.click(screen.getByRole('button', { name: 'Yes, cancel registration' }))

    expect(await screen.findByText(WITHDRAW_CLOSED_EN)).toHaveAttribute('role', 'status')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows an alert and keeps the registration when the withdrawal fails otherwise', async () => {
    const user = userEvent.setup()
    eventsWith('Confirmed', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:internal', title: 'Internal', status: 500, traceId: 'trace' },
        { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: 'Cancelar inscrição' }))
    await user.click(screen.getByRole('button', { name: 'Sim, cancelar inscrição' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText('Você saiu deste encontro.')).not.toBeInTheDocument()
    expect(screen.queryByText(WITHDRAW_CLOSED_PT)).not.toBeInTheDocument()
  })

  it('offers no withdrawal once the event has started, as the server would refuse it', async () => {
    const past = '0199f1a2-3b4c-7d5e-8f60-718293a4b5c8'
    const served = new Set<string>()
    const event = (id: string, startsAt: string) => ({
      eventId: id,
      title: `Evento ${id}`,
      startsAt,
      timeZone: 'UTC',
      checkInOpensAt: startsAt,
      state: 'Published',
    })
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      http.get(`${API}/events`, () =>
        HttpResponse.json([event(past, '2020-01-01T00:00:00Z'), event(eventId, '2099-01-01T00:00:00Z')]),
      ),
      http.get(`${API}/events/:id/participation`, ({ params }) => {
        served.add(String(params.id))
        return HttpResponse.json({ state: 'Confirmed', roomId: null })
      }),
    )
    await renderApp('/events')
    expect(await screen.findByRole('button', { name: 'Cancelar inscrição' })).toBeInTheDocument()
    await waitFor(() => {
      expect(served).toEqual(new Set([past, eventId]))
    })
    const pastEntry = screen.getByRole('heading', { name: `Evento ${past}` }).closest('section')
    if (!(pastEntry instanceof HTMLElement)) {
      throw new Error('Evento passado sem seção.')
    }
    expect(within(pastEntry).queryByRole('button', { name: 'Cancelar inscrição' })).not.toBeInTheDocument()
  })

  it('closes the confirmation and keeps focus on the event when it starts meanwhile', async () => {
    const startsAt = Date.parse('2099-01-01T00:00:00Z')
    const now = vi.spyOn(Date, 'now').mockReturnValue(startsAt - 60_000)
    try {
      const user = userEvent.setup()
      const deletes = eventsWith('Confirmed')
      await renderApp('/events')
      await user.click(await screen.findByRole('button', { name: 'Cancelar inscrição' }))
      expect(screen.getByText(/não poderá se inscrever de novo/)).toHaveFocus()

      now.mockReturnValue(startsAt)

      await waitFor(
        () => {
          expect(screen.queryByText(/não poderá se inscrever de novo/)).not.toBeInTheDocument()
        },
        { timeout: 5000 },
      )
      expect(screen.getByText(WITHDRAW_CLOSED_PT)).toHaveAttribute('role', 'status')
      expect(screen.getByText(WITHDRAW_CLOSED_PT)).toHaveFocus()
      expect(screen.queryByRole('button', { name: 'Cancelar inscrição' })).not.toBeInTheDocument()
      expect(deletes).toHaveLength(0)
    } finally {
      now.mockRestore()
    }
  })

  it.each([
    ['confirmed', '2026-11-10T00:00:00.001Z', true],
    ['confirmed', '2026-11-10T00:00:00.000Z', false],
    ['waitlisted', '2026-11-10T00:00:01Z', true],
    ['withdrawn', '2026-11-10T00:00:01Z', false],
    [undefined, '2026-11-10T00:00:01Z', false],
  ])('decides withdrawal for %s with startsAt %s', (state, startsAt, expected) => {
    expect(canWithdrawRegistration(state, startsAt, Date.parse('2026-11-10T00:00:00Z'))).toBe(expected)
  })
})
