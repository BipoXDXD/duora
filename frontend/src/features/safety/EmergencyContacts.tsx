import { useI18n } from '@/shared/i18n/i18n-context'
import { TextLink } from '@/shared/ui/page'
import { EMERGENCY_CONTACT_COUNTRIES } from './emergency-contacts'

/** Public, immediately visible information; no location, account or network lookup is required. */
export function EmergencyContacts(): React.JSX.Element {
  const { t } = useI18n()
  return (
    <section id="safety" aria-labelledby="safety-title" tabIndex={-1} className="scroll-mt-4">
      <h2 id="safety-title" className="text-xl font-semibold text-text">
        {t('emergency.title')}
      </h2>
      <p className="mt-2 text-sm text-text-muted">{t('emergency.countryGuidance')}</p>
      <div className="mt-4 grid gap-6 sm:grid-cols-2">
        {EMERGENCY_CONTACT_COUNTRIES.map((country) => (
          <section key={country.code} aria-labelledby={`safety-${country.code}`}>
            <h3 id={`safety-${country.code}`} className="font-semibold text-text">
              {t(country.titleKey)}
            </h3>
            <ul className="mt-2 flex flex-col items-start gap-1 text-sm">
              {country.contacts.map((contact) => (
                <li key={contact.number}>
                  <TextLink to={`tel:${contact.number}`}>{t(contact.labelKey, { number: contact.number })}</TextLink>
                </li>
              ))}
            </ul>
            <ul className="mt-2 flex flex-col items-start text-sm">
              {country.sources.map((source) => (
                <li key={source.url}>
                  <TextLink to={source.url}>{t(source.labelKey)}</TextLink>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </section>
  )
}
