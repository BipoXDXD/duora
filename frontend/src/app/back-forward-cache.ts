interface BackForwardCacheGuard {
  /** Normalmente `window`; recebe `pagehide` e `pageshow`. */
  readonly target: EventTarget
  /** Tira a página de vista antes de ela ir para o bfcache. */
  readonly hide: () => void
  readonly reload: () => void
}

/**
 * O bfcache devolve a página inteira, com DOM e memória, ao voltar no navegador, sem perguntar nada ao servidor:
 * se a sessão expirou ou outra pessoa entrou nesse meio-tempo, a tela de antes voltaria com os dados de quem a
 * deixou (T-21, T-14). A página é escondida ao entrar no bfcache e, se for restaurada, recarrega; a recarga
 * consulta a identidade no servidor. O custo é perder o estado em memória ao voltar de outro site.
 * Devolve a função que remove os listeners.
 */
export function reloadWhenRestoredFromBackForwardCache({ target, hide, reload }: BackForwardCacheGuard): () => void {
  const onPageHide = (event: Event): void => {
    if (isPersisted(event)) {
      hide()
    }
  }
  const onPageShow = (event: Event): void => {
    if (isPersisted(event)) {
      reload()
    }
  }
  target.addEventListener('pagehide', onPageHide)
  target.addEventListener('pageshow', onPageShow)
  return () => {
    target.removeEventListener('pagehide', onPageHide)
    target.removeEventListener('pageshow', onPageShow)
  }
}

function isPersisted(event: Event): boolean {
  return event instanceof PageTransitionEvent && event.persisted
}
