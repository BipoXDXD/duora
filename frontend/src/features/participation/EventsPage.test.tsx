import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server } from '@/test/msw'
import { renderApp } from '@/test/render'

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
const ROUND_CANCELLED_PT = 'A rodada foi cancelada. Sua inscrição continua valendo e você não precisa fazer nada.'
const ROUND_CANCELLED_EN =
  'The round was cancelled. Your registration still stands and there is nothing you need to do.'

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
    const register = screen.getByRole('button', { name: 'Inscrever-se' })
    register.focus()

    body = { state: 'Waiting', roomId: null, roundCancelled: true }
    await queryClient.refetchQueries()

    expect(await screen.findByText(ROUND_CANCELLED_PT)).toBeInTheDocument()
    expect(register).toHaveFocus()
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
