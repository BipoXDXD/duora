import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { renderApp } from '@/test/render'

const aliases = [
  ['/criar-conta', '/register'],
  ['/cadastro', '/register'],
  ['/entrar', '/login'],
  ['/verifique-seu-email', '/check-email'],
  ['/esqueci-a-senha', '/forgot-password'],
  ['/recuperar-senha', '/forgot-password'],
] as const

describe('legacy public routes', () => {
  it.each(aliases)('%s redirects to %s with query and fragment', async (oldPath, canonical) => {
    await renderApp(`${oldPath}?source=email#help`)
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(`${canonical}?source=email#help`))
  })
  it.each(['/confirmar-email', '/confirm-email'])('consumes a legacy token at %s', async (path) => {
    await renderApp(`${path}#userId=0199f1a2-3b4c-7d5e-8f60-718293a4b5c6&token=tok%2Ben%2F%3D`)
    expect(await screen.findByLabelText('Senha', { exact: true })).toBeVisible()
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/confirm-email$/))
  })
})

it('preserves the login notice across the legacy redirect', async () => {
  await renderApp({ pathname: '/entrar', state: { notice: 'signed-out' } })
  expect(await screen.findByRole('status')).toHaveTextContent('Você saiu da sua conta.')
})
