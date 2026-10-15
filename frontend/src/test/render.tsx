import type { ReactNode } from 'react'
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { render, type RenderResult } from '@testing-library/react'
import userEvent, { type UserEvent } from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { AppRoutes } from '@/app/AppRoutes'
import { createQueryClient } from '@/app/query-client'

/** QueryClient de produção, sem espera entre novas tentativas para o teste não depender de relógio. */
export function createTestQueryClient(): QueryClient {
  const queryClient = createQueryClient()
  const defaults = queryClient.getDefaultOptions()
  queryClient.setDefaultOptions({ ...defaults, queries: { ...defaults.queries, retryDelay: 0 } })
  return queryClient
}

interface RouteEntry {
  readonly pathname: string
  readonly search?: string
  readonly state?: unknown
}

/** Mostra o caminho atual num elemento oculto, para o teste conferir navegações. */
function LocationProbe(): React.JSX.Element {
  const location = useLocation()
  return <output data-testid="location" hidden>{`${location.pathname}${location.search}${location.hash}`}</output>
}

export interface RenderedApp extends RenderResult {
  readonly user: UserEvent
  readonly queryClient: QueryClient
}

export function renderApp(entry: string | RouteEntry, queryClient: QueryClient = createTestQueryClient()): RenderedApp {
  const user = userEvent.setup()
  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[entry]}>
        <AppRoutes />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...view, user, queryClient }
}

export function TestProviders({
  queryClient,
  initialPath = '/',
  children,
}: {
  readonly queryClient: QueryClient
  readonly initialPath?: string
  readonly children: ReactNode
}): React.JSX.Element {
  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialPath]}>
        {children}
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  )
}
