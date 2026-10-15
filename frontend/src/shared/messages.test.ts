// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { formatWait } from './messages'

describe('formatWait', () => {
  it.each([
    [null, 'alguns minutos'],
    [0, '1 segundo'],
    [1, '1 segundo'],
    [2, '2 segundos'],
    [59, '59 segundos'],
    [60, '1 minuto'],
    [61, '2 minutos'],
    [120, '2 minutos'],
    [900, '15 minutos'],
  ])('%s segundos → "%s"', (seconds, expected) => {
    expect(formatWait(seconds)).toBe(expected)
  })
})
