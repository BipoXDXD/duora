import { testPrivateOperation } from '@/test/private-context'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server } from '@/test/msw'
import { fetchEvents } from './participation-api'

function events(startsAt: string) {
  server.use(
    http.get(`${API}/events`, () =>
      HttpResponse.json([
        {
          eventId: 'event',
          title: 'Synthetic',
          startsAt,
          timeZone: 'America/Sao_Paulo',
          checkInOpensAt: '2026-10-16T22:50:00Z',
          state: 'Published',
        },
      ]),
    ),
  )
}

describe('instantes de eventos na fronteira da API', () => {
  it.each(['2026-10-16', '2026-10-16T20:00:00'])(
    'recusa %s sem inventar fuso para uma data civil ou hora local',
    async (value) => {
      events(value)
      await expect(fetchEvents(testPrivateOperation())).rejects.toThrow()
    },
  )
  it('preserva um instante com offset explícito e o fuso IANA separado', async () => {
    events('2026-10-16T20:00:00-03:00')
    expect(await fetchEvents(testPrivateOperation())).toEqual([
      {
        eventId: 'event',
        title: 'Synthetic',
        startsAt: '2026-10-16T20:00:00-03:00',
        timeZone: 'America/Sao_Paulo',
        checkInOpensAt: '2026-10-16T22:50:00Z',
        state: 'Published',
      },
    ])
  })
})
