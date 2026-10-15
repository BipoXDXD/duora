import { useEffect, useState } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'

/** Abaixo disto o chunk costuma chegar antes de o aviso ser lido; mostrar o texto só faria a tela piscar. */
export const ROUTE_FALLBACK_DELAY_MS = 400

/**
 * Espera do chunk de uma tela na primeira carga. A região de status existe desde o início, vazia, e o texto
 * entra só depois do atraso, então leitores de tela anunciam a espera longa e a curta não pisca.
 * Navegações dentro da SPA não chegam aqui: o React Router navega numa transition e a tela anterior
 * continua visível, com o foco onde estava, até a nova estar pronta.
 */
export function RouteFallback(): React.JSX.Element {
  const { t } = useI18n()
  const [slow, setSlow] = useState(false)

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSlow(true)
    }, ROUTE_FALLBACK_DELAY_MS)
    return () => {
      window.clearTimeout(timer)
    }
  }, [])

  return (
    <p role="status" data-testid="carregando-rota" className="text-text-muted">
      {slow ? t('navigation.loadingPage') : null}
    </p>
  )
}
