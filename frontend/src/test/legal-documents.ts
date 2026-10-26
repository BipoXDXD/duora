/** Explicit synthetic publication. It is never loaded by the application bundle. */
export const syntheticLegalCatalog = {
  registrationAvailable: true,
  termsVersion: 'test-terms-v2',
  privacyVersion: 'test-privacy-v3',
  documents: (['terms', 'privacy'] as const).flatMap((document, kindIndex) =>
    (['pt-BR', 'en-US'] as const).map((locale, localeIndex) => ({
      document,
      version: document === 'terms' ? 'test-terms-v2' : 'test-privacy-v3',
      locale,
      title: document === 'terms' ? 'Synthetic Terms' : 'Synthetic Privacy',
      controllerName: 'Synthetic Test Controller',
      privacyContact: 'privacy@example.test',
      published: true,
      sha256: String(kindIndex * 2 + localeIndex + 1).repeat(64),
      sections: [{ title: 'Synthetic section', paragraphs: [`Synthetic ${document} ${locale} text.`] }],
    })),
  ),
}
