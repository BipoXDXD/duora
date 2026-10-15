import { useEffect, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { readEmailLinkFragment, type EmailLink } from './auth-schemas'

/**
 * Lê `userId` e `token` do fragmento do link de e-mail uma única vez, guarda em estado e limpa a
 * URL (fragmento e qualquer query) com replace. O token não fica na barra de endereço nem no
 * histórico, e continua disponível para o POST.
 */
export function useEmailLinkFromUrl(): EmailLink | null {
  const location = useLocation()
  const navigate = useNavigate()
  const [link] = useState(() => readEmailLinkFragment(location.hash))

  const { pathname, search, hash } = location
  const state: unknown = location.state
  useEffect(() => {
    if (search !== '' || hash !== '') {
      void navigate({ pathname }, { replace: true, state })
    }
  }, [pathname, search, hash, state, navigate])

  return link
}
