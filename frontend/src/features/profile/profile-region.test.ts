// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { en } from '@/shared/i18n/catalog.en-US'
import { ptBR } from '@/shared/i18n/catalog.pt-BR'
import { REGION_CODES, REGION_LABEL_KEYS } from './profile-region'

describe('regiões do perfil', () => {
  it('são as 78 subdivisões de BR e US, sem repetição', () => {
    expect(REGION_CODES).toHaveLength(78)
    expect(new Set(REGION_CODES).size).toBe(78)
    expect(REGION_CODES.every((code) => /^(BR|US)-[A-Z]{2}$/.test(code))).toBe(true)
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
    expect(ptBR[REGION_LABEL_KEYS['US-DC']]).toBe('Distrito de Colúmbia')
    expect(ptBR[REGION_LABEL_KEYS['US-WA']]).toBe('Washington')
  })
})
