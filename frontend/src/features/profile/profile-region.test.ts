// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { en } from '@/shared/i18n/catalog.en-US'
import { ptBR } from '@/shared/i18n/catalog.pt-BR'
import { REGION_CODES, REGION_LABEL_KEYS } from './profile-region'

describe('regiões do perfil', () => {
  it('são as 27 UFs em ISO 3166-2:BR, sem repetição', () => {
    expect(REGION_CODES).toHaveLength(27)
    expect(new Set(REGION_CODES).size).toBe(27)
    expect(REGION_CODES.every((code) => /^BR-[A-Z]{2}$/.test(code))).toBe(true)
  })

  it('cada UF tem nome nos dois catálogos', () => {
    const missing = REGION_CODES.filter((code) => {
      const key = REGION_LABEL_KEYS[code]
      return ptBR[key].trim() === '' || en[key].trim() === ''
    })

    expect(missing).toEqual([])
  })

  it('usa o nome oficial em português e o nome usual em inglês', () => {
    expect(ptBR[REGION_LABEL_KEYS['BR-SP']]).toBe('São Paulo')
    expect(ptBR[REGION_LABEL_KEYS['BR-DF']]).toBe('Distrito Federal')
    expect(en[REGION_LABEL_KEYS['BR-DF']]).toBe('Federal District')
  })
})
