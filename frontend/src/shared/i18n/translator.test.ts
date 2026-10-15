// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createDateTimeFormatter, createTranslator } from './translator'

describe('createTranslator', () => {
  it('traduz uma mensagem sem argumentos no idioma pedido', () => {
    expect(createTranslator('pt-BR')('login.title')).toBe('Entrar')
    expect(createTranslator('en')('login.title')).toBe('Sign in')
  })

  it('interpola argumentos sem concatenar no código', () => {
    const t = createTranslator('en')

    expect(t('register.passwordHint', { min: 12, max: 128 })).toBe(
      'Between 12 and 128 characters. A long phrase is easier to remember.',
    )
    expect(t('app.documentTitle', { page: 'Sign in' })).toBe('Sign in · Duora')
  })

  it('chave inexistente e valores errados não compilam', () => {
    const t = createTranslator('pt-BR')
    // Nunca executada: o typecheck confere cada @ts-expect-error.
    const invalidCalls = () => [
      // @ts-expect-error chave fora do catálogo
      t('login.titulo'),
      // @ts-expect-error mensagem com argumentos exige os valores
      t('register.passwordHint'),
      // @ts-expect-error argumento com nome errado
      t('register.passwordHint', { min: 12, maximo: 128 }),
      // @ts-expect-error plural exige número
      t('common.rateLimited', { unit: 'seconds', count: 'dois' }),
      // @ts-expect-error mensagem sem argumentos não aceita valores
      t('login.title', { extra: 1 }),
    ]

    expect(invalidCalls).toBeTypeOf('function')
  })

  it('reaproveita o formatador da mesma chave sem misturar valores', () => {
    const t = createTranslator('pt-BR')

    expect(t('navigation.pageLoaded', { title: 'A' })).toBe('Página carregada: A')
    expect(t('navigation.pageLoaded', { title: 'B' })).toBe('Página carregada: B')
  })
})

describe('createDateTimeFormatter', () => {
  const instant = new Date('2026-10-15T13:05:00Z')

  it('formata data e hora no padrão do idioma, no fuso indicado', () => {
    expect(createDateTimeFormatter('pt-BR', 'UTC')(instant)).toBe('15/10/2026, 13:05')
    expect(createDateTimeFormatter('en', 'UTC')(instant)).toBe('10/15/26, 1:05 PM')
  })

  it('o fuso muda a hora exibida, não o instante', () => {
    expect(createDateTimeFormatter('pt-BR', 'America/Sao_Paulo')(instant)).toBe('15/10/2026, 10:05')
  })
})
