// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createDateTimeFormatter, createEventDateTimeFormatter, createTranslator } from './translator'

describe('createTranslator', () => {
  it('traduz uma mensagem sem argumentos no idioma pedido', () => {
    expect(createTranslator('pt-BR')('login.title')).toBe('Entrar')
    expect(createTranslator('en-US')('login.title')).toBe('Sign in')
  })

  it('interpola argumentos sem concatenar no código', () => {
    const t = createTranslator('en-US')

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
    expect(createDateTimeFormatter('pt-BR', 'UTC')(instant)).toBe('15/10/2026, 13:05 UTC')
    expect(createDateTimeFormatter('en-US', 'UTC')(instant)).toBe('10/15/26, 1:05 PM UTC')
  })

  it('o fuso muda a hora exibida, não o instante', () => {
    expect(createDateTimeFormatter('pt-BR', 'America/Sao_Paulo')(instant)).toBe('15/10/2026, 10:05 BRT')
  })
})

// Literais conferidos no Node 24.21.0 / ICU 78.3; não derivar expected com Intl.
describe('transições de horário de verão', () => {
  const instants = ['2026-03-08T06:59:00Z', '2026-03-08T07:00:00Z', '2026-11-01T05:30:00Z', '2026-11-01T06:30:00Z']
  it.each([
    [
      'pt-BR',
      'America/New_York',
      ['08/03/2026, 01:59 GMT-5', '08/03/2026, 03:00 GMT-4', '01/11/2026, 01:30 GMT-4', '01/11/2026, 01:30 GMT-5'],
    ],
    [
      'en-US',
      'America/New_York',
      ['3/8/26, 1:59 AM EST', '3/8/26, 3:00 AM EDT', '11/1/26, 1:30 AM EDT', '11/1/26, 1:30 AM EST'],
    ],
    [
      'pt-BR',
      'America/Sao_Paulo',
      ['08/03/2026, 03:59 BRT', '08/03/2026, 04:00 BRT', '01/11/2026, 02:30 BRT', '01/11/2026, 03:30 BRT'],
    ],
    [
      'en-US',
      'America/Sao_Paulo',
      ['3/8/26, 3:59 AM GMT-3', '3/8/26, 4:00 AM GMT-3', '11/1/26, 2:30 AM GMT-3', '11/1/26, 3:30 AM GMT-3'],
    ],
    [
      'pt-BR',
      'UTC',
      ['08/03/2026, 06:59 UTC', '08/03/2026, 07:00 UTC', '01/11/2026, 05:30 UTC', '01/11/2026, 06:30 UTC'],
    ],
    ['en-US', 'UTC', ['3/8/26, 6:59 AM UTC', '3/8/26, 7:00 AM UTC', '11/1/26, 5:30 AM UTC', '11/1/26, 6:30 AM UTC']],
  ] as const)('%s em %s identifica o fuso sem mudar o instante', (locale, zone, expected) => {
    const format = createDateTimeFormatter(locale, zone)
    instants.forEach((value, index) => {
      const instant = new Date(value)
      expect(format(instant)).toBe(expected[index])
      expect(instant.toISOString()).toBe(value.replace('Z', '.000Z'))
    })
  })
})

describe('horário do evento e do navegador', () => {
  const instant = new Date('2026-10-16T23:00:00Z')
  it('identifica ambos os fusos com mensagens traduzidas', () => {
    expect(createEventDateTimeFormatter('pt-BR', 'America/New_York')(instant, 'America/Sao_Paulo')).toBe(
      'Horário do evento: 16/10/2026, 20:00 BRT (16/10/2026, 19:00 GMT-4 no fuso do seu navegador)',
    )
    expect(createEventDateTimeFormatter('en-US', 'America/New_York')(instant, 'America/Sao_Paulo')).toBe(
      'Event time: 10/16/26, 8:00 PM GMT-3 (10/16/26, 7:00 PM EDT in your browser’s time zone)',
    )
  })
  it('não duplica fusos iguais, mas distingue fusos com o mesmo deslocamento', () => {
    expect(createEventDateTimeFormatter('en-US', 'America/New_York')(instant, 'America/New_York')).toBe(
      'Event time: 10/16/26, 7:00 PM EDT',
    )
    expect(createEventDateTimeFormatter('en-US', 'America/Toronto')(instant, 'America/New_York')).toContain(
      'in your browser’s time zone',
    )
  })
  it('mostra a data correta quando a conversão cruza a meia-noite', () => {
    expect(createEventDateTimeFormatter('pt-BR', 'America/New_York')(new Date('2026-10-17T02:30:00Z'), 'UTC')).toBe(
      'Horário do evento: 17/10/2026, 02:30 UTC (16/10/2026, 22:30 GMT-4 no fuso do seu navegador)',
    )
  })
  it('não inventa um fuso para eventos legados', () => {
    expect(createEventDateTimeFormatter('pt-BR', 'UTC')(instant, null)).toBe(
      'Referência: 16/10/2026, 23:00 UTC; fuso do evento não informado',
    )
  })
})
