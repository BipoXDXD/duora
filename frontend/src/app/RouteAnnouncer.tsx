import { useLayoutEffect, useRef } from 'react'
import { useLocation } from 'react-router'
import { useI18n } from '@/shared/i18n/i18n-context'

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
  const { t } = useI18n()
  const previousPathname = useRef(pathname)
  const regionRef = useRef<HTMLParagraphElement>(null)

  useLayoutEffect(() => {
    if (previousPathname.current === pathname) {
      return
    }
    previousPathname.current = pathname
    const main = document.getElementById(mainId)
    const announce = (): boolean => {
      if (main?.querySelector('[data-route-pending]')) return false
      focusPageStart(mainId)
      if (regionRef.current !== null)
        regionRef.current.textContent = t('navigation.pageLoaded', { title: document.title })
      return true
    }
    const observer = new MutationObserver(() => {
      if (announce()) observer.disconnect()
    })
    if (!announce() && main !== null)
      observer.observe(main, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['data-route-pending'],
      })
    return () => {
      observer.disconnect()
    }
    // Só a troca de rota anuncia: trocar o idioma não é navegação, então `t` fica fora das dependências.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
