import { useEffect } from 'react'

/**
 * Pede confirmação ao fechar ou recarregar a página. Só escuta enquanto há trabalho pendente, porque um listener de
 * `beforeunload` permanente tiraria a página do bfcache. Não cobre navegação dentro da SPA.
 */
export function useLeaveWarning(active: boolean): void {
  useEffect(() => {
    if (!active) return undefined
    const warn = (event: BeforeUnloadEvent): void => {
      event.preventDefault()
    }
    window.addEventListener('beforeunload', warn)
    return () => {
      window.removeEventListener('beforeunload', warn)
    }
  }, [active])
}
