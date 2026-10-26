/** Official national numbers. Language never selects a country; both groups are always shown. */
export const EMERGENCY_CONTACT_COUNTRIES = [
  {
    code: 'BR',
    titleKey: 'emergency.brazil',
    contacts: [
      { number: '190', labelKey: 'emergency.brazilPolice' },
      { number: '192', labelKey: 'emergency.brazilMedical' },
      { number: '180', labelKey: 'emergency.brazilWomen' },
    ],
    sources: [
      { url: 'https://www.gov.br/mulheres/pt-br/ligue180', labelKey: 'emergency.brazilWomenSource' },
      { url: 'https://www.gov.br/saude/pt-br/composicao/saes/samu-192', labelKey: 'emergency.brazilMedicalSource' },
    ],
  },
  {
    code: 'US',
    titleKey: 'emergency.unitedStates',
    contacts: [{ number: '911', labelKey: 'emergency.unitedStatesEmergency' }],
    sources: [{ url: 'https://www.911.gov/calling-911/', labelKey: 'emergency.unitedStatesSource' }],
  },
] as const
