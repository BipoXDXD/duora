import { screen } from '@testing-library/react'
import { renderWithI18n } from '@/test/render'
import { describe, expect, it } from 'vitest'
import { Alert, RequestFailureAlert } from './alert'

describe('Alert', () => {
  it('erro é anunciado como alert, com título em texto além da cor', () => {
    renderWithI18n(<Alert variant="error">E-mail ou senha incorretos.</Alert>)

    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('Não deu certo')
    expect(alert).toHaveTextContent('E-mail ou senha incorretos.')
  })

  it('aviso é anunciado como status, sem interromper', () => {
    renderWithI18n(<Alert variant="info">Sua sessão expirou.</Alert>)

    expect(screen.getByRole('status')).toHaveTextContent('AvisoSua sessão expirou.')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('sucesso é anunciado como status', () => {
    renderWithI18n(<Alert variant="success">Senha alterada.</Alert>)

    expect(screen.getByRole('status')).toHaveTextContent('ProntoSenha alterada.')
  })
})

describe('Alert com focusOnMount', () => {
  it('recebe o foco ao aparecer, sem entrar na ordem de tabulação', () => {
    renderWithI18n(
      <Alert variant="success" focusOnMount>
        Senha alterada.
      </Alert>,
    )

    const alert = screen.getByRole('status')
    expect(alert).toHaveFocus()
    expect(alert).toHaveAttribute('tabindex', '-1')
  })

  it('sem a opção, não rouba o foco', () => {
    renderWithI18n(<Alert variant="info">Aviso.</Alert>)

    expect(screen.getByRole('status')).not.toHaveFocus()
    expect(screen.getByRole('status')).not.toHaveAttribute('tabindex')
  })
})

describe('RequestFailureAlert', () => {
  it('mostra a mensagem de rede, distinta das demais', () => {
    renderWithI18n(<RequestFailureAlert failure={{ kind: 'network' }} />)

    expect(screen.getByRole('alert')).toHaveTextContent('Não foi possível falar com o Duora.')
  })

  it('mostra o tempo de espera do limite de uso', () => {
    renderWithI18n(<RequestFailureAlert failure={{ kind: 'rate-limited', retryAfterSeconds: 120 }} />)

    expect(screen.getByRole('alert')).toHaveTextContent('espere 2 minutos antes de tentar de novo')
  })

  it('traduz o code conhecido pela tabela da tela', () => {
    renderWithI18n(
      <RequestFailureAlert
        failure={{ kind: 'problem', status: 400, code: 'INVALID_TOKEN' }}
        problemMessages={{ INVALID_TOKEN: 'confirmEmail.invalidToken' }}
      />,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Link inválido ou senha incorreta.')
  })

  it.each([
    ['code herdado constructor', { kind: 'problem', status: 400, code: 'constructor' }],
    ['code herdado __proto__', { kind: 'problem', status: 400, code: '__proto__' }],
    ['code herdado toString', { kind: 'problem', status: 400, code: 'toString' }],
    ['code desconhecido', { kind: 'problem', status: 400, code: 'OUTRO' }],
    ['problem sem code', { kind: 'problem', status: 500, code: null }],
    ['falha inesperada', { kind: 'unexpected' }],
  ] as const)('usa a mensagem genérica para %s', (_caso, failure) => {
    renderWithI18n(
      <RequestFailureAlert failure={failure} problemMessages={{ INVALID_TOKEN: 'confirmEmail.invalidToken' }} />,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Algo deu errado do nosso lado.')
  })
})
