import type { components } from '@/shared/api/schema'
import type { PlainMessageKey } from '@/shared/i18n/catalog'

/** UF em ISO 3166-2:BR, na forma canônica (`BR-SP`): o enum do contrato, gerado da spec. */
export type RegionCode = NonNullable<components['schemas']['OwnProfileResponse']['regionCode']>

/**
 * Nome de cada UF no catálogo. O `Record` sobre a união gerada obriga uma entrada por código: se o contrato ganhar ou
 * perder uma UF, isto não compila até o catálogo acompanhar.
 */
export const REGION_LABEL_KEYS = {
  'BR-AC': 'profile.region.BR-AC',
  'BR-AL': 'profile.region.BR-AL',
  'BR-AM': 'profile.region.BR-AM',
  'BR-AP': 'profile.region.BR-AP',
  'BR-BA': 'profile.region.BR-BA',
  'BR-CE': 'profile.region.BR-CE',
  'BR-DF': 'profile.region.BR-DF',
  'BR-ES': 'profile.region.BR-ES',
  'BR-GO': 'profile.region.BR-GO',
  'BR-MA': 'profile.region.BR-MA',
  'BR-MG': 'profile.region.BR-MG',
  'BR-MS': 'profile.region.BR-MS',
  'BR-MT': 'profile.region.BR-MT',
  'BR-PA': 'profile.region.BR-PA',
  'BR-PB': 'profile.region.BR-PB',
  'BR-PE': 'profile.region.BR-PE',
  'BR-PI': 'profile.region.BR-PI',
  'BR-PR': 'profile.region.BR-PR',
  'BR-RJ': 'profile.region.BR-RJ',
  'BR-RN': 'profile.region.BR-RN',
  'BR-RO': 'profile.region.BR-RO',
  'BR-RR': 'profile.region.BR-RR',
  'BR-RS': 'profile.region.BR-RS',
  'BR-SC': 'profile.region.BR-SC',
  'BR-SE': 'profile.region.BR-SE',
  'BR-SP': 'profile.region.BR-SP',
  'BR-TO': 'profile.region.BR-TO',
} as const satisfies Readonly<Record<RegionCode, PlainMessageKey>>

/** As 27 UFs, na ordem das chaves acima; `z.enum` pede uma tupla não vazia. */
export const REGION_CODES = Object.keys(REGION_LABEL_KEYS) as [RegionCode, ...RegionCode[]]
