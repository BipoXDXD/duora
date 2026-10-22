import type { components } from '@/shared/api/schema'
import type { PlainMessageKey } from '@/shared/i18n/catalog'

/** Subdivisão de primeiro nível; o catálogo inicial preserva somente as 27 UFs. */
export type RegionCode = NonNullable<components['schemas']['OwnProfileResponse']['subdivisionCode']>

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
  'US-AK': 'profile.region.US-AK',
  'US-AL': 'profile.region.US-AL',
  'US-AR': 'profile.region.US-AR',
  'US-AZ': 'profile.region.US-AZ',
  'US-CA': 'profile.region.US-CA',
  'US-CO': 'profile.region.US-CO',
  'US-CT': 'profile.region.US-CT',
  'US-DC': 'profile.region.US-DC',
  'US-DE': 'profile.region.US-DE',
  'US-FL': 'profile.region.US-FL',
  'US-GA': 'profile.region.US-GA',
  'US-HI': 'profile.region.US-HI',
  'US-IA': 'profile.region.US-IA',
  'US-ID': 'profile.region.US-ID',
  'US-IL': 'profile.region.US-IL',
  'US-IN': 'profile.region.US-IN',
  'US-KS': 'profile.region.US-KS',
  'US-KY': 'profile.region.US-KY',
  'US-LA': 'profile.region.US-LA',
  'US-MA': 'profile.region.US-MA',
  'US-MD': 'profile.region.US-MD',
  'US-ME': 'profile.region.US-ME',
  'US-MI': 'profile.region.US-MI',
  'US-MN': 'profile.region.US-MN',
  'US-MO': 'profile.region.US-MO',
  'US-MS': 'profile.region.US-MS',
  'US-MT': 'profile.region.US-MT',
  'US-NC': 'profile.region.US-NC',
  'US-ND': 'profile.region.US-ND',
  'US-NE': 'profile.region.US-NE',
  'US-NH': 'profile.region.US-NH',
  'US-NJ': 'profile.region.US-NJ',
  'US-NM': 'profile.region.US-NM',
  'US-NV': 'profile.region.US-NV',
  'US-NY': 'profile.region.US-NY',
  'US-OH': 'profile.region.US-OH',
  'US-OK': 'profile.region.US-OK',
  'US-OR': 'profile.region.US-OR',
  'US-PA': 'profile.region.US-PA',
  'US-RI': 'profile.region.US-RI',
  'US-SC': 'profile.region.US-SC',
  'US-SD': 'profile.region.US-SD',
  'US-TN': 'profile.region.US-TN',
  'US-TX': 'profile.region.US-TX',
  'US-UT': 'profile.region.US-UT',
  'US-VA': 'profile.region.US-VA',
  'US-VT': 'profile.region.US-VT',
  'US-WA': 'profile.region.US-WA',
  'US-WI': 'profile.region.US-WI',
  'US-WV': 'profile.region.US-WV',
  'US-WY': 'profile.region.US-WY',
} as const satisfies Readonly<Record<RegionCode, PlainMessageKey>>

/** As 27 UFs, na ordem das chaves acima; `z.enum` pede uma tupla não vazia. */
export const COUNTRY_CODES = ['BR', 'US'] as const
export type CountryCode = (typeof COUNTRY_CODES)[number]
export function isCountryCode(value: string): value is CountryCode {
  return value === 'BR' || value === 'US'
}

export const REGION_CODES = Object.keys(REGION_LABEL_KEYS) as [RegionCode, ...RegionCode[]]

export function isRegionCode(value: string): value is RegionCode {
  return Object.hasOwn(REGION_LABEL_KEYS, value)
}
