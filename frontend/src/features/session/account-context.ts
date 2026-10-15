import { createContext, useContext } from 'react'
import type { Me } from './session-api'

export const AccountContext = createContext<Me | null>(null)

/** Conta da sessão corrente; só existe abaixo de RequireSession, que garante a identidade. */
export function useAccount(): Me {
  const account = useContext(AccountContext)
  if (account === null) {
    throw new Error('useAccount usado fora de RequireSession.')
  }
  return account
}
