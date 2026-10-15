import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AppRoutes } from './AppRoutes'

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AppRoutes />
    </MemoryRouter>,
  )
}

describe('shell da aplicação', () => {
  it('expõe os landmarks banner, main e contentinfo', () => {
    renderAt('/')

    expect(screen.getByRole('banner')).toBeInTheDocument()
    expect(screen.getByRole('main')).toBeInTheDocument()
    expect(screen.getByRole('contentinfo')).toBeInTheDocument()
  })

  it('oferece como primeiro elemento focável um link para pular ao conteúdo principal', async () => {
    renderAt('/')

    await userEvent.tab()

    const skipLink = screen.getByRole('link', { name: 'Pular para o conteúdo' })
    expect(skipLink).toHaveFocus()
    expect(skipLink).toHaveAttribute('href', `#${screen.getByRole('main').id}`)
  })

  it('mostra o nome Duora no cabeçalho como link para o início', () => {
    renderAt('/')

    expect(within(screen.getByRole('banner')).getByRole('link', { name: 'Duora' })).toHaveAttribute('href', '/')
  })
})

describe('página inicial', () => {
  it('tem um único título de nível 1 e define o título do documento', () => {
    renderAt('/')

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(document.title).toBe('Duora')
  })
})

describe('rota inexistente', () => {
  it('informa que a página não foi encontrada e oferece o caminho de volta', () => {
    renderAt('/nao-existe')

    expect(screen.getByRole('heading', { level: 1, name: 'Página não encontrada' })).toBeInTheDocument()
    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Voltar ao início' })).toHaveAttribute(
      'href',
      '/',
    )
    expect(document.title).toBe('Página não encontrada · Duora')
  })
})

describe('página inicial: entrada', () => {
  it('oferece criar conta e entrar', () => {
    renderAt('/')

    const main = screen.getByRole('main')
    expect(within(main).getByRole('link', { name: 'Criar conta' })).toHaveAttribute('href', '/criar-conta')
    expect(within(main).getByRole('link', { name: 'Entrar' })).toHaveAttribute('href', '/entrar')
  })
})
