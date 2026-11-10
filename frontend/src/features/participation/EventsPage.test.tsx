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
