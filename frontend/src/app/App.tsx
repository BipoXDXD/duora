import { useState } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router'
import { AppRoutes } from './AppRoutes'
import { createQueryClient } from './query-client'

export function App(): React.JSX.Element {
  // Um QueryClient por montagem da aplicação, nunca compartilhado entre árvores ou testes.
  const [queryClient] = useState(createQueryClient)

  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </QueryClientProvider>
  )
}
