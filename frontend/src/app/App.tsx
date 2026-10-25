import { SecurityResultNavigationGuard } from '@/features/auth/SecurityCompletionNotice'
import { useEffect, useState } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router'
import { apiClient } from '@/shared/api'
import { identityBroadcast } from '@/features/session/identity-broadcast'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import { resolveInitialLocale, type Locale } from '@/shared/i18n/locale'
import { loadLocalePreference } from '@/shared/i18n/locale-preference'
import { AppRoutes } from './AppRoutes'
import { createQueryClient } from './query-client'

function detectInitialLocale(): Locale {
  return resolveInitialLocale({ stored: loadLocalePreference(), preferred: navigator.languages })
}

export function App(): React.JSX.Element {
  // Um QueryClient por montagem da aplicação, nunca compartilhado entre árvores ou testes.
  const [queryClient] = useState(createQueryClient)
  const [initialLocale] = useState(detectInitialLocale)

  // Outra aba entrou ou saiu: o cookie já é de outra identidade (ou de nenhuma). Os dados em tela somem e a
  // identidade é reconsultada; a rota protegida decide entre a conta nova e a entrada (T-21).
  useEffect(
    () =>
      identityBroadcast.subscribe(() => {
        apiClient.sessionContext.protect('identity')
      }),
    [queryClient],
  )

  return (
    <I18nProvider initialLocale={initialLocale}>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <SecurityResultNavigationGuard />
          <AppRoutes />
        </BrowserRouter>
      </QueryClientProvider>
    </I18nProvider>
  )
}
