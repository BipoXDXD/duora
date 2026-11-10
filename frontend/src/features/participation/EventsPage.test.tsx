import { withSessionContext } from '@/test/private-context'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

    expect(screen.getByText(/não poderá se inscrever de novo neste encontro/)).toBeInTheDocument()
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
    await user.click(screen.getByRole('button', { name: 'Confirmar saída' }))

    expect(await screen.findByText('Você saiu deste encontro.')).toBeInTheDocument()
    expect(deletes).toHaveLength(1)
    expect(await deletes[0]?.text()).toBe('')
    expect(screen.queryByRole('button', { name: 'Inscrever-se' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar presença' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Sair da lista/ })).not.toBeInTheDocument()
  })

  it('shows an alert and keeps the registration when the server refuses', async () => {
    const user = userEvent.setup()
    eventsWith('Confirmed', () =>
      HttpResponse.json(
        { type: 'urn:duora:problem:not-found', title: 'Not Found', status: 404, traceId: 'trace' },
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    await renderApp('/events')
    await user.click(await screen.findByRole('button', { name: 'Cancelar inscrição' }))
    await user.click(screen.getByRole('button', { name: 'Confirmar saída' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText('Você saiu deste encontro.')).not.toBeInTheDocument()
  })
})
