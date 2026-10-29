import type { OperationHandle } from '@/shared/session-context'
import { sendMutation } from '@/shared/api'
import type { components } from '@/shared/api/schema'
import type { LegalDocument } from './legal-publication'

type AcceptTermsRequest = components['schemas']['AcceptTermsRequest']

/**
 * Novo aceite dos Termos ativos pela conta da sessão (API-A17), com a declaração de 18 anos ou mais. O recibo é o
 * do documento exibido (versão, idioma e SHA-256 devolvidos pelo servidor). Repetir é inofensivo: 204 de novo.
 */
export function acceptCurrentTerms(operation: OperationHandle, terms: LegalDocument): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/me/terms-acceptances',
    body: {
      acceptedTermsVersion: terms.version,
      acceptedTermsLocale: terms.locale,
      acceptedTermsSha256: terms.sha256,
      // The form only submits with the box checked; the literal keeps the contract explicit.
      confirmedAdult: true,
    } satisfies AcceptTermsRequest,
  })
}
