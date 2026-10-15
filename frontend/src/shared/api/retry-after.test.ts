// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { parseRetryAfterSeconds } from './retry-after'

const NOW = Date.parse('2026-10-15T12:00:00Z')

describe('parseRetryAfterSeconds', () => {
  it.each([
    ['0', 0],
    ['1', 1],
    ['120', 120],
    [' 30 ', 30],
  ])('lê delta-seconds "%s"', (header, expected) => {
    expect(parseRetryAfterSeconds(header, NOW)).toBe(expected)
  })

  it('converte HTTP-date futura em segundos arredondados para cima', () => {
    expect(parseRetryAfterSeconds('Thu, 15 Oct 2026 12:01:30 GMT', NOW + 500)).toBe(90)
  })

  it('trata HTTP-date no passado como zero', () => {
    expect(parseRetryAfterSeconds('Thu, 15 Oct 2026 11:00:00 GMT', NOW)).toBe(0)
  })

  it.each([
    ['ausente', null],
    ['vazio', ''],
    ['negativo', '-5'],
    ['fracionário', '1.5'],
    ['texto', 'logo'],
    ['data inválida', 'Thu, 99 Foo 2026'],
  ])('devolve null quando o header é %s', (_caso, header) => {
    expect(parseRetryAfterSeconds(header, NOW)).toBeNull()
  })
})
