import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { CheckboxField, PasswordField, TextField } from './form-fields'

describe('TextField', () => {
  it('associa o label persistente ao campo', () => {
    render(<TextField label="E-mail" name="email" type="email" autoComplete="email" />)

    const input = screen.getByLabelText('E-mail')
    expect(input).toHaveAttribute('type', 'email')
    expect(input).toHaveAttribute('autocomplete', 'email')
    expect(input).toHaveAttribute('name', 'email')
  })

  it('descreve o campo pela ajuda quando não há erro', () => {
    render(<TextField label="E-mail" hint="Use um e-mail só seu." />)

    const input = screen.getByLabelText('E-mail')
    expect(input).toHaveAccessibleDescription('Use um e-mail só seu.')
    expect(input).not.toHaveAttribute('aria-invalid')
  })

  it('marca o campo inválido e associa o erro em texto pela descrição', () => {
    render(<TextField label="E-mail" hint="Use um e-mail só seu." error="Informe seu e-mail." />)

    const input = screen.getByLabelText('E-mail')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(input).toHaveAccessibleDescription('Use um e-mail só seu. Informe seu e-mail.')
    expect(screen.getByText('Informe seu e-mail.')).toBeVisible()
  })

  it('permite colar no campo', async () => {
    render(<TextField label="E-mail" />)
    const input = screen.getByLabelText('E-mail')

    input.focus()
    await userEvent.paste('ana@exemplo.com')

    expect(input).toHaveValue('ana@exemplo.com')
  })
})

describe('PasswordField', () => {
  it('começa oculto, com o autocomplete pedido', () => {
    render(<PasswordField label="Senha" autoComplete="current-password" />)

    const input = screen.getByLabelText('Senha')
    expect(input).toHaveAttribute('type', 'password')
    expect(input).toHaveAttribute('autocomplete', 'current-password')
  })

  it('o botão mostrar senha é um toggle acessível que revela e oculta', async () => {
    render(<PasswordField label="Senha" autoComplete="new-password" />)
    const toggle = screen.getByRole('button', { name: 'Mostrar senha' })
    const input = screen.getByLabelText('Senha')

    expect(toggle).toHaveAttribute('aria-pressed', 'false')
    expect(toggle).toHaveAttribute('aria-controls', input.id)

    await userEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-pressed', 'true')
    expect(input).toHaveAttribute('type', 'text')

    await userEvent.click(toggle)
    expect(input).toHaveAttribute('type', 'password')
  })

  it('o toggle não submete o formulário', async () => {
    let submitted = false
    render(
      <form
        onSubmit={(event) => {
          event.preventDefault()
          submitted = true
        }}
      >
        <PasswordField label="Senha" />
      </form>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Mostrar senha' }))

    expect(submitted).toBe(false)
  })

  it('permite colar a senha', async () => {
    render(<PasswordField label="Senha" />)
    const input = screen.getByLabelText('Senha')

    input.focus()
    await userEvent.paste('frase longa de senha colada')

    expect(input).toHaveValue('frase longa de senha colada')
  })

  it('associa o erro ao campo de senha', () => {
    render(<PasswordField label="Senha" error="Informe sua senha." />)

    const input = screen.getByLabelText('Senha')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(input).toHaveAccessibleDescription('Informe sua senha.')
  })
})

describe('CheckboxField', () => {
  it('é uma caixa de seleção com label clicável', async () => {
    render(<CheckboxField label="Aceito os termos" />)
    const checkbox = screen.getByRole('checkbox', { name: 'Aceito os termos' })

    await userEvent.click(screen.getByText('Aceito os termos'))

    expect(checkbox).toBeChecked()
  })

  it('associa o erro à caixa de seleção', () => {
    render(<CheckboxField label="Aceito os termos" error="Aceite os termos." />)

    const checkbox = screen.getByRole('checkbox', { name: 'Aceito os termos' })
    expect(checkbox).toHaveAttribute('aria-invalid', 'true')
    expect(checkbox).toHaveAccessibleDescription('Aceite os termos.')
  })
})
