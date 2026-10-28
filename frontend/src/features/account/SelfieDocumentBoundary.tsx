import { useEffect } from 'react'
import { useLocation } from 'react-router'
import { paths } from '@/shared/paths'
import { isSelfieDocument, reloadCurrentDocument } from './selfie-document'

/**
 * O documento de /account/selfie carrega a política da borda que libera câmera e o componente da AWS. Ao sair dele
 * por navegação interna, o destino é recarregado para voltar à política comum.
 */
export function SelfieDocumentBoundary(): null {
  const { pathname } = useLocation()
  useEffect(() => {
    if (pathname !== paths.selfie && isSelfieDocument()) reloadCurrentDocument()
  }, [pathname])
  return null
}
