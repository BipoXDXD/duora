import { useEffect, useRef } from 'react'
import { useLocation } from 'react-router'
import { messages } from '@/shared/messages'

interface RouteAnnouncerProps {
  /** Id do `<main>`, onde o foco cai quando a página nova ainda não tem h1. */
  readonly mainId: string
}

/**
 * Numa SPA o navegador não avisa a troca de página (A11Y-02). A cada navegação o foco vai para o
 * h1 da página nova (ou para o main) e uma região aria-live educada anuncia o título.
 * A primeira carga fica de fora: aí o navegador já anuncia o documento.
 */
export function RouteAnnouncer({ mainId }: RouteAnnouncerProps): React.JSX.Element {
  const { pathname } = useLocation()
  const previousPathname = useRef(pathname)
  const regionRef = useRef<HTMLParagraphElement>(null)

  useEffect(() => {
    if (previousPathname.current === pathname) {
      return
    }
    previousPathname.current = pathname
    focusPageStart(mainId)
    if (regionRef.current !== null) {
      regionRef.current.textContent = messages.navigation.pageLoaded(document.title)
    }
  }, [pathname, mainId])

  // aria-live sem role=status: a região não concorre com os avisos de cada tela.
  return <p ref={regionRef} aria-live="polite" aria-atomic="true" data-testid="anuncio-de-rota" className="sr-only" />
}

function focusPageStart(mainId: string): void {
  const main = document.getElementById(mainId)
  const target = main?.querySelector<HTMLElement>('h1') ?? main
  if (target === null) {
    return
  }
  if (!target.hasAttribute('tabindex')) {
    target.setAttribute('tabindex', '-1')
  }
  target.focus()
}
