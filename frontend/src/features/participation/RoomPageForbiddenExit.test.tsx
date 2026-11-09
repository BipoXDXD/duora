import { withSessionContext } from '@/test/private-context'
import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const me = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'test@example.com',
  status: 'Restricted',
  emailConfirmed: true,
  mfaEnabled: false,
  roles: ['participant'],
}

function forbiddenRead(code: string) {
  return http.get(`${API}/rooms/room-1`, () =>
    HttpResponse.json(
      { type: 'about:blank', title: 'Forbidden', status: 403, code },
      { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  )
}

describe('saída da sala quando a leitura é recusada', () => {
  it('oferece a saída a quem deixou de ser elegível e confirma o encerramento', async () => {
    let exits = 0
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      forbiddenRead('FORBIDDEN'),
      http.post(`${API}/rooms/room-1/exit`, () => {
        exits += 1
        return new HttpResponse(null, { status: 204 })
      }),
    )
    useAntiforgeryEndpoint()

    const { user } = await renderApp('/rooms/room-1')
    await user.click(await screen.findByRole('button', { name: 'Sair da sala' }))

    expect(await screen.findByText('Este encontro foi encerrado.')).toBeInTheDocument()
    await waitFor(() => {
      expect(exits).toBe(1)
    })
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('não oferece a saída a conta privilegiada, que a rota recusa', async () => {
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json(withSessionContext({ ...me, mfaEnabled: true, roles: ['moderator'] })),
      ),
      forbiddenRead('FORBIDDEN'),
    )
    useAntiforgeryEndpoint()

    await renderApp('/rooms/room-1')

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
  })

  it('não oferece a saída à conta suspensa, que o portão continua recusando', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(withSessionContext(me))),
      forbiddenRead('ACCOUNT_SUSPENDED'),
    )
    useAntiforgeryEndpoint()

    await renderApp('/rooms/room-1')

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sair da sala' })).not.toBeInTheDocument()
  })
})
