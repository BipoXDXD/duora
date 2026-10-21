// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { readLoginState } from './login-state'

describe('readLoginState', () => {
  it('lê aviso e destino interno', () => {
    expect(readLoginState({ notice: 'session-expired', from: '/conta?aba=sessoes' })).toEqual({
      notice: 'session-expired',
      from: '/account?aba=sessoes',
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

describe('canonical safe returns', () => {
  it.each([
    ['/conta/perfil?tab=photos#section', '/account/profile?tab=photos#section'],
    ['/account/preferences', '/account/preferences'],
    ['/events', '/events'],
    ['/rooms/0199f1a2-3b4c-7d5e-8f60-718293a4b5c6', '/rooms/0199f1a2-3b4c-7d5e-8f60-718293a4b5c6'],
    ['/conta?token=secret&userId=private&tab=sessions#token=secret', '/account?tab=sessions'],
  ])('%s becomes %s', (from, expected) => {
    expect(readLoginState({ from })).toEqual({ from: expected })
  })
  it.each([
    '/\\evil.example/account',
    '/\t/evil.example/account',
    '/%2f%2fevil.example',
    '/account/../login',
    '/account\\profile',
    '/rooms/not-an-id',
    '/confirm-email',
    '/login?next=/account',
  ])('rejects %s', (from) => {
    expect(readLoginState({ from })).toEqual({})
  })
})
