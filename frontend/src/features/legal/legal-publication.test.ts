import { describe, expect, it } from 'vitest'
import { syntheticLegalCatalog } from '@/test/legal-documents'
import { legalCatalogSchema, registrationReceipt } from './legal-publication'

describe('published legal identity', () => {
  it('binds independent document languages and their exact identities', () => {
    const catalog = legalCatalogSchema.parse(syntheticLegalCatalog)
    const receipt = registrationReceipt(catalog, 'pt-BR', 'en-US')
    expect(receipt?.terms.locale).toBe('pt-BR')
    expect(receipt?.privacy.locale).toBe('en-US')
    expect(receipt?.terms.sha256).toBe('1'.repeat(64))
    expect(receipt?.privacy.sha256).toBe('4'.repeat(64))
  })
  it('rejects false readiness, missing translations and duplicate identities', () => {
    const catalog = legalCatalogSchema.parse(syntheticLegalCatalog)
    expect(registrationReceipt({ ...catalog, registrationAvailable: false }, 'pt-BR', 'pt-BR')).toBeNull()
    expect(registrationReceipt({ ...catalog, documents: catalog.documents.slice(1) }, 'pt-BR', 'pt-BR')).toBeNull()
    expect(
      legalCatalogSchema.safeParse({ ...catalog, documents: [...catalog.documents, catalog.documents[0]] }).success,
    ).toBe(false)
  })
  it('cannot register under a draft or an unidentified controller even if readiness is asserted', () => {
    const catalog = legalCatalogSchema.parse(syntheticLegalCatalog)
    for (const change of [{ published: false }, { controllerName: null }, { privacyContact: null }]) {
      expect(
        registrationReceipt(
          { ...catalog, documents: catalog.documents.map((document) => ({ ...document, ...change })) },
          'pt-BR',
          'pt-BR',
        ),
      ).toBeNull()
    }
  })
})
