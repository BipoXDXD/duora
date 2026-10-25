import type { Translate } from '@/shared/i18n/translator'

type LegalText =
  | 'terms'
  | 'privacy'
  | 'navigation'
  | 'loading'
  | 'unavailable'
  | 'notFound'
  | 'draft'
  | 'controller'
  | 'unknownController'
  | 'contact'
  | 'version'
  | 'language'
  | 'fingerprint'
  | 'termsLanguage'
  | 'privacyLanguage'
  | 'acknowledgement'
export function legalCopy(t: Translate): Readonly<Record<LegalText, string>> {
  return {
    terms: t('legal.terms'),
    privacy: t('legal.privacy'),
    navigation: t('legal.navigation'),
    loading: t('legal.loading'),
    unavailable: t('legal.unavailable'),
    notFound: t('legal.notFound'),
    draft: t('legal.draft'),
    controller: t('legal.controller'),
    unknownController: t('legal.unknownController'),
    contact: t('legal.contact'),
    version: t('legal.version'),
    language: t('legal.language'),
    fingerprint: t('legal.fingerprint'),
    termsLanguage: t('legal.termsLanguage'),
    privacyLanguage: t('legal.privacyLanguage'),
    acknowledgement: t('legal.acknowledgement'),
  }
}
