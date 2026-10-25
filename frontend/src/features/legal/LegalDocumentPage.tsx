import { useParams } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Page, TextLink } from '@/shared/ui/page'
import { legalCopy } from './legal-copy'
import { legalDocumentPath, useLegalPublication, type LegalKind } from './legal-publication'
import { LegalNavigation } from './LegalNavigation'

export function TermsPage(): React.JSX.Element {
  return <LegalDocumentPage kind="terms" />
}
export function PrivacyPage(): React.JSX.Element {
  return <LegalDocumentPage kind="privacy" />
}
function LegalDocumentPage({ kind }: { readonly kind: LegalKind }): React.JSX.Element {
  const { version, documentLocale } = useParams()
  const { locale, t } = useI18n()
  const copy = legalCopy(t)
  const publication = useLegalPublication()
  const currentVersion = kind === 'terms' ? publication.data?.termsVersion : publication.data?.privacyVersion
  const document = publication.data?.documents.find(
    (candidate) =>
      candidate.document === kind &&
      candidate.version === (version ?? currentVersion) &&
      candidate.locale === (documentLocale ?? locale),
  )
  return (
    <Page title={document?.title ?? copy[kind]} width="wide">
      <LegalNavigation />
      {publication.isPending ? (
        <p role="status">{copy.loading}</p>
      ) : !document ? (
        <p role="alert">{copy.notFound}</p>
      ) : (
        <>
          {!document.published ? <p role="status">{copy.draft}</p> : null}
          <div lang={document.locale} className="flex flex-col gap-6">
            <dl lang={locale}>
              <dt>{copy.version}</dt>
              <dd>{document.version}</dd>
              <dt>{copy.language}</dt>
              <dd>{document.locale}</dd>
              <dt>{copy.controller}</dt>
              <dd>{document.controllerName ?? copy.unknownController}</dd>
              <dt>{copy.contact}</dt>
              <dd>{document.privacyContact ?? '—'}</dd>
              <dt>{copy.fingerprint}</dt>
              <dd className="font-mono text-sm break-all">{document.sha256}</dd>
            </dl>
            {document.sections.map((section, index) => (
              <section key={index} className="flex flex-col gap-3">
                <h2 className="text-xl font-semibold">{section.title}</h2>
                {section.paragraphs.map((paragraph, paragraphIndex) => (
                  <p key={paragraphIndex}>{paragraph}</p>
                ))}
              </section>
            ))}
          </div>
          <nav aria-label={copy.language} className="flex flex-wrap gap-4">
            {publication.data?.documents
              .filter((candidate) => candidate.document === kind && candidate.version === document.version)
              .map((candidate) => (
                <TextLink key={candidate.locale} to={legalDocumentPath(candidate)} lang={candidate.locale}>
                  {candidate.locale}
                </TextLink>
              ))}
          </nav>
        </>
      )}
    </Page>
  )
}
