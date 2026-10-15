// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { readLoginState } from './login-state'

describe('readLoginState', () => {
  it('lê aviso e destino interno', () => {
    expect(readLoginState({ notice: 'session-expired', from: '/conta?aba=sessoes' })).toEqual({
      notice: 'session-expired',
      from: '/conta?aba=sessoes',
    })
  })

  it.each([
    ['null', null],
    ['texto', 'qualquer'],
    ['aviso desconhecido', { notice: 'hacked' }],
  ])('ignora estado inválido: %s', (_caso, state) => {
    expect(readLoginState(state)).toEqual({})
  })

  it.each([
    ['caminho interno fora da lista', '/admin/usuarios'],
    ['rota de autenticação', '/entrar'],
    ['rota de link de e-mail', '/redefinir-senha'],
    ['prefixo parecido', '/contas'],
  ])('recusa destino fora da allowlist: %s', (_caso, from) => {
    expect(readLoginState({ from })).toEqual({})
  })

  it('aceita o início como destino', () => {
    expect(readLoginState({ from: '/' })).toEqual({ from: '/' })
  })

  it.each(['https://evil.example/', '//evil.example/conta', '/\\evil.example', 'conta', ''])(
    'recusa destino que não é caminho interno: %s',
    (from) => {
      expect(readLoginState({ from })).toEqual({})
    },
  )
})
