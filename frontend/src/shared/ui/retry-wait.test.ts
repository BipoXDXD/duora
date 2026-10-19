// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createTranslator } from '@/shared/i18n/translator'
import { retryWait } from './retry-wait'

describe('aviso de limite de tentativas', () => {
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
  ])('pt-BR: Retry-After %s → "%s"', (seconds, wait) => {
    expect(createTranslator('pt-BR')('common.rateLimited', retryWait(seconds))).toBe(
      `Muitas tentativas seguidas. Por segurança, espere ${wait} antes de tentar de novo.`,
    )
  })

  it.each([
    [null, 'a few minutes'],
    [1, '1 second'],
    [2, '2 seconds'],
    [60, '1 minute'],
    [61, '2 minutes'],
  ])('en-US: Retry-After %s → "%s"', (seconds, wait) => {
    expect(createTranslator('en-US')('common.rateLimited', retryWait(seconds))).toBe(
      `Too many attempts in a row. For your security, wait ${wait} before trying again.`,
    )
  })
})
