import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

describe('explicit confirmation request', () => {
  it('waits for explicit credentials and submit, refreshes CSRF and clears the password after neutral acceptance', async () => {
    const seen: { body: unknown; csrf: string | null }[] = []
    const csrf = useAntiforgeryEndpoint()
    server.use(
      http.post(`${API}/auth/email-confirmation-requests`, async ({ request }) => {
        seen.push({ body: await request.json(), csrf: request.headers.get('X-XSRF-TOKEN') })
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const { user } = await renderApp('/check-email')
    expect(seen).toEqual([])
    await user.type(screen.getByLabelText('E-mail'), 'owner@example.com')
    await user.type(screen.getByLabelText('Senha'), 'senha já escolhida')
    expect(seen).toEqual([])
    await user.click(screen.getByRole('button', { name: 'Pedir outro código' }))
    await screen.findByText(/Se o cadastro ainda puder ser confirmado/)
    expect(seen).toEqual([
      { body: { email: 'owner@example.com', password: 'senha já escolhida' }, csrf: csrf.latestToken() },
    ])
    expect(screen.queryByLabelText('Senha')).not.toBeInTheDocument()
    expect(window.location.href).not.toContain('owner%40')
    expect(window.location.href).not.toContain('senha')
  })

  it('does not send a blank form and clears the password after a throttled request', async () => {
    useAntiforgeryEndpoint()
    let count = 0
    server.use(
      http.post(`${API}/auth/email-confirmation-requests`, () => {
        count++
        return problem(429, 'TOO_MANY_REQUESTS')
      }),
    )
    const { user } = await renderApp('/check-email')
    await user.click(screen.getByRole('button', { name: 'Pedir outro código' }))
    expect(count).toBe(0)
    await user.type(screen.getByLabelText('E-mail'), 'owner@example.com')
    await user.type(screen.getByLabelText('Senha'), 'senha já escolhida')
    await user.click(screen.getByRole('button', { name: 'Pedir outro código' }))
    await waitFor(() => expect(screen.getByLabelText('Senha')).toHaveValue(''))
    expect(count).toBe(1)
    expect(screen.getByLabelText('E-mail')).toHaveValue('owner@example.com')
    expect(screen.queryByText(/Se o cadastro ainda puder ser confirmado/)).not.toBeInTheDocument()
  })
})
