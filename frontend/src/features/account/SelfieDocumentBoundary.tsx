import { useEffect, type ReactNode } from 'react'
import { useLocation } from 'react-router'
import { paths } from '@/shared/paths'
import { isSelfieDocument, reloadCurrentDocument } from './selfie-document'

/**
 * O documento de /account/selfie carrega a política da borda que libera câmera e o componente da AWS. Ao sair dele
 * por navegação interna, o destino é recarregado para voltar à política comum. O destino não renderiza antes disso:
 * as leituras que ele dispararia seriam canceladas pelo recarregamento, e o WebKit registra cada uma como erro.
 */
export function SelfieDocumentBoundary({ children }: { readonly children: ReactNode }): ReactNode {
  const { pathname } = useLocation()
  const leavingSelfieDocument = pathname !== paths.selfie && isSelfieDocument()
  useEffect(() => {
    if (leavingSelfieDocument) reloadCurrentDocument()
  }, [leavingSelfieDocument, pathname])
  return leavingSelfieDocument ? null : children
}
