import { installTestMe } from '@/test/private-context'
import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { AccountContext } from '@/features/session/account-context'
import { I18nProvider } from '@/shared/i18n/I18nProvider'
import { RegionSuggestion } from './RegionSuggestion'

vi.mock('./region-detection-api', () => ({
  fetchRegionDetectionCapability: () => Promise.resolve(true),
  suggestRegion: () => Promise.resolve({ status: 'disabled', countryCode: null, observedAt: '2026-10-21T12:00:00Z' }),
}))

it('removes the available action when POST reports disabled after an enabled capability', async () => {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const me = installTestMe({
    id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
    email: 'a@example.com',
    status: 'Active',
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
    preferredLocale: null,
    preferredTimeZone: null,
  })
  render(
    <I18nProvider initialLocale="pt-BR">
      <QueryClientProvider client={queries}>
        <PrivateOperationProvider owner={me.owner}>
          <AccountContext value={me}>
            <RegionSuggestion onCountry={() => true} />
          </AccountContext>
        </PrivateOperationProvider>
      </QueryClientProvider>
    </I18nProvider>,
  )
  fireEvent.click(await screen.findByRole('button', { name: 'Sugerir país pela conexão' }))
  await screen.findByText('A sugestão está desativada. Escolha manualmente.')
  expect(screen.queryByRole('button', { name: 'Sugerir país pela conexão' })).not.toBeInTheDocument()
})
