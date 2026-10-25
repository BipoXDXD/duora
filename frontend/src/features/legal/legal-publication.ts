import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { z } from 'zod'
import { apiClient } from '@/shared/api'
import { runPublicOperation } from '@/features/session/public-operation'
import type { Locale } from '@/shared/i18n/locale'

const version = z.string().regex(/^[A-Za-z0-9._-]{1,32}$/)
const nonempty = z.string().refine((text) => text.trim().length > 0)
const legalDocumentSchema = z.object({
  document: z.enum(['terms', 'privacy']),
  version,
  locale: z.enum(['pt-BR', 'en-US']),
  title: nonempty,
  controllerName: nonempty.nullable(),
  privacyContact: z.email().nullable(),
  published: z.boolean(),
  sha256: z.string().regex(/^[a-f0-9]{64}$/),
  sections: z.array(z.object({ title: nonempty, paragraphs: z.array(nonempty).min(1) })).min(1),
})
export type LegalDocument = z.output<typeof legalDocumentSchema>
export type LegalKind = LegalDocument['document']
export const legalCatalogSchema = z
  .object({
    registrationAvailable: z.boolean(),
    termsVersion: version,
    privacyVersion: version,
    documents: z.array(legalDocumentSchema),
  })
  .superRefine((catalog, context) => {
    const keys = catalog.documents.map((document) => `${document.document}/${document.version}/${document.locale}`)
    if (new Set(keys).size !== keys.length) context.addIssue({ code: 'custom', message: 'Duplicate document identity' })
  })
export type LegalCatalog = z.output<typeof legalCatalogSchema>
export interface LegalReceipt {
  readonly terms: LegalDocument
  readonly privacy: LegalDocument
}

export function registrationReceipt(
  catalog: LegalCatalog,
  termsLocale: Locale,
  privacyLocale: Locale,
): LegalReceipt | null {
  const active = catalog.documents.filter(
    (document) => document.version === (document.document === 'terms' ? catalog.termsVersion : catalog.privacyVersion),
  )
  if (
    !catalog.registrationAvailable ||
    active.length !== 4 ||
    active.some((document) => !document.published || !document.controllerName || !document.privacyContact) ||
    new Set(active.map((document) => document.controllerName)).size !== 1 ||
    new Set(active.map((document) => document.privacyContact)).size !== 1
  )
    return null
  const terms = active.find((document) => document.document === 'terms' && document.locale === termsLocale)
  const privacy = active.find((document) => document.document === 'privacy' && document.locale === privacyLocale)
  return terms && privacy ? { terms, privacy } : null
}

export function legalDocumentPath(document: Pick<LegalDocument, 'document' | 'version' | 'locale'>): string {
  return `/${document.document}/${encodeURIComponent(document.version)}/${document.locale}`
}

export function useLegalPublication(): UseQueryResult<LegalCatalog> {
  return useQuery({
    queryKey: ['public-legal-documents'],
    staleTime: 0,
    retry: false,
    queryFn: ({ signal }) =>
      runPublicOperation(async (operation) =>
        legalCatalogSchema.parse(
          await apiClient.requestJson({ operation, method: 'GET', path: '/api/v1/legal-documents', signal }),
        ),
      ),
  })
}
