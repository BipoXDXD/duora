import type { ReactElement, ReactNode } from 'react'
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { render, screen, waitFor, type RenderResult } from '@testing-library/react'
import userEvent, { type UserEvent } from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { AppRoutes } from '@/app/AppRoutes'
import { createQueryClient } from '@/app/query-client'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import type { Locale } from '@/shared/i18n/locale'

/**
 * Idioma fixo dos testes: o jsdom anuncia `en-US` em `navigator.languages`, e a detecção é testada
 * à parte (App.test.tsx). Assim cada teste diz em que idioma espera os textos.
 */
const TEST_LOCALE: Locale = 'pt-BR'

/** Componente isolado com o idioma disponível, para o que usa `useI18n`. */
export function renderWithI18n(ui: ReactElement, locale: Locale = TEST_LOCALE): RenderResult {
  return render(<I18nProvider initialLocale={locale}>{ui}</I18nProvider>)
}

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

/**
 * Monta a SPA na rota pedida e espera o chunk da tela chegar (as telas são carregadas sob demanda),
 * para o teste começar pela tela já renderizada.
 */
export async function renderApp(
  entry: string | RouteEntry,
  queryClient: QueryClient = createTestQueryClient(),
  locale: Locale = TEST_LOCALE,
): Promise<RenderedApp> {
  const user = userEvent.setup()
  const view = render(
    <I18nProvider initialLocale={locale}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[entry]}>
          <AppRoutes />
          <LocationProbe />
        </MemoryRouter>
      </QueryClientProvider>
    </I18nProvider>,
  )
  await waitForRouteChunk()
  return { ...view, user, queryClient }
}

/** Espera o fallback de rota sair: o chunk da tela chegou e ela já está renderizada. */
export async function waitForRouteChunk(): Promise<void> {
  await waitFor(() => {
    if (screen.queryByTestId('carregando-rota') !== null) {
      throw new Error('a tela da rota ainda está carregando')
    }
  })
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
    <I18nProvider initialLocale={TEST_LOCALE}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[initialPath]}>
          {children}
          <LocationProbe />
        </MemoryRouter>
      </QueryClientProvider>
    </I18nProvider>
  )
}
