import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LOCALE_STORAGE_KEY } from '@/shared/i18n/locale-preference'
import { App } from './App'

function useBrowserLanguages(languages: readonly string[]): void {
  vi.spyOn(navigator, 'languages', 'get').mockReturnValue(languages)
}

function renderAppAt(path: string) {
  window.history.pushState({}, '', path)
  const user = userEvent.setup()
  return { user, ...render(<App />) }
}

function languageGroup(): HTMLElement {
  return within(screen.getByRole('banner')).getByRole('group')
}

beforeEach(() => {
  document.documentElement.lang = 'pt-BR'
})

afterEach(() => {
  window.localStorage.clear()
  window.history.pushState({}, '', '/')
})

describe('idioma inicial', () => {
  it('segue o navegador quando ele prefere inglês', () => {
    useBrowserLanguages(['en-US', 'pt-BR'])
    renderAppAt('/')

    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Create account' })).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('en')
  })

  it('idioma não suportado cai para pt-BR', () => {
    useBrowserLanguages(['fr-FR', 'de-DE'])
    renderAppAt('/')

    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Criar conta' })).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('pt-BR')
  })

  it('a escolha guardada vence o idioma do navegador', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en')
    useBrowserLanguages(['pt-BR'])
    renderAppAt('/entrar')

    expect(screen.getByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(document.title).toBe('Sign in · Duora')
  })

  it('valor guardado fora da lista é ignorado', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, '<script>')
    useBrowserLanguages(['pt-BR'])
    renderAppAt('/')

    expect(document.documentElement.lang).toBe('pt-BR')
  })
})

describe('seletor de idioma', () => {
  it('trocar para inglês atualiza textos, título, lang e guarda a escolha', async () => {
    useBrowserLanguages(['pt-BR'])
    const { user } = renderAppAt('/entrar')
    expect(document.title).toBe('Entrar · Duora')

    await user.click(within(languageGroup()).getByRole('button', { name: 'English' }))

    expect(screen.getByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByLabelText('Email', { exact: true })).toBeInTheDocument()
    expect(document.title).toBe('Sign in · Duora')
    expect(document.documentElement.lang).toBe('en')
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en')
  })

  it('é um grupo rotulado no idioma ativo, que marca o idioma atual com aria-pressed', async () => {
    useBrowserLanguages(['pt-BR'])
    const { user } = renderAppAt('/')

    expect(languageGroup()).toHaveAccessibleName('Idioma')
    const portuguese = within(languageGroup()).getByRole('button', { name: 'Português' })
    const english = within(languageGroup()).getByRole('button', { name: 'English' })
    expect(portuguese).toHaveAttribute('aria-pressed', 'true')
    expect(english).toHaveAttribute('aria-pressed', 'false')
    expect(english).toHaveAttribute('lang', 'en')

    await user.click(english)

    expect(languageGroup()).toHaveAccessibleName('Language')
    expect(english).toHaveAttribute('aria-pressed', 'true')
    expect(portuguese).toHaveAttribute('aria-pressed', 'false')
  })

  it('funciona só com teclado', async () => {
    useBrowserLanguages(['pt-BR'])
    const { user } = renderAppAt('/')
    const english = within(languageGroup()).getByRole('button', { name: 'English' })

    while (document.activeElement !== english) {
      await user.tab()
    }
    await user.keyboard('{Enter}')

    expect(document.documentElement.lang).toBe('en')
    expect(english).toHaveFocus()
  })

  it('volta para português e guarda a nova escolha', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en')
    const { user } = renderAppAt('/')

    await user.click(within(languageGroup()).getByRole('button', { name: 'Português' }))

    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Entrar' })).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('pt-BR')
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('pt-BR')
  })

  it('sem armazenamento disponível, a troca vale para a aba', async () => {
    useBrowserLanguages(['pt-BR'])
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('bloqueado', 'SecurityError')
    })
    const { user } = renderAppAt('/')

    await user.click(within(languageGroup()).getByRole('button', { name: 'English' }))

    expect(document.documentElement.lang).toBe('en')
    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('erro de validação já exibido muda de idioma junto com a interface', async () => {
    useBrowserLanguages(['pt-BR'])
    const { user } = renderAppAt('/entrar')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))
    expect(await screen.findByText('Informe seu e-mail.')).toBeInTheDocument()

    await user.click(within(languageGroup()).getByRole('button', { name: 'English' }))

    expect(screen.getByText('Enter your email.')).toBeInTheDocument()
    expect(screen.queryByText('Informe seu e-mail.')).not.toBeInTheDocument()
  })
})
