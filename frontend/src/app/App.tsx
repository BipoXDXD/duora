import { useState } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router'
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

  return (
    <I18nProvider initialLocale={initialLocale}>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </QueryClientProvider>
    </I18nProvider>
  )
}
