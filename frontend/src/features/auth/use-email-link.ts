import { useEffect, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { readEmailLink, type EmailLink } from './auth-schemas'

/**
 * Lê `userId` e `token` do link de e-mail uma única vez, guarda em estado e tira a query da URL.
 * Assim o token não fica na barra de endereço, no histórico nem no Referer das requisições
 * seguintes, e continua disponível para o POST.
 */
export function useEmailLinkFromUrl(): EmailLink | null {
  const location = useLocation()
  const navigate = useNavigate()
  const [link] = useState(() => readEmailLink(new URLSearchParams(location.search)))

  const { pathname, search, hash } = location
  const state: unknown = location.state
  useEffect(() => {
    if (search !== '') {
      void navigate({ pathname, hash }, { replace: true, state })
    }
  }, [pathname, search, hash, state, navigate])

  return link
}
