import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Button } from './button'

describe('Button', () => {
  it('é um botão do tipo button por padrão, para não submeter formulários sem intenção', () => {
    render(<Button>Entrar</Button>)

    expect(screen.getByRole('button', { name: 'Entrar' })).toHaveAttribute('type', 'button')
  })

  it('aceita type submit explícito', () => {
    render(<Button type="submit">Enviar</Button>)

    expect(screen.getByRole('button', { name: 'Enviar' })).toHaveAttribute('type', 'submit')
  })

  it('aciona onClick pelo teclado', async () => {
    const onClick = vi.fn()
    render(<Button onClick={onClick}>Confirmar</Button>)

    screen.getByRole('button', { name: 'Confirmar' }).focus()
    await userEvent.keyboard('{Enter}')

    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('não aciona onClick quando desabilitado', async () => {
    const onClick = vi.fn()
    render(
      <Button disabled onClick={onClick}>
        Confirmar
      </Button>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar' }))

    expect(onClick).not.toHaveBeenCalled()
  })

  it('renderiza o filho como link quando asChild, sem aninhar button em a', () => {
    render(
      <Button asChild>
        <a href="/eventos">Ver eventos</a>
      </Button>,
    )

    const link = screen.getByRole('link', { name: 'Ver eventos' })
    expect(link).toHaveAttribute('href', '/eventos')
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
