import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it } from 'vitest'
import { AccountTimeZoneProvider } from './AccountTimeZoneProvider'
import { I18nProvider } from './I18nProvider'
import { useI18n } from './i18n-context'

function Probe(): React.JSX.Element {
  const { formatEventDateTime, changeLocale } = useI18n()
  return (
    <>
      <output>{formatEventDateTime(new Date('2026-11-01T06:30:00Z'), 'UTC')}</output>
      <button
        onClick={() => {
          changeLocale('en-US')
        }}
      >
        English
      </button>
    </>
  )
}
function View({ zone }: { readonly zone: string | null }): React.JSX.Element {
  return (
    <I18nProvider initialLocale="pt-BR">
      <AccountTimeZoneProvider timeZone={zone}>
        <Probe />
      </AccountTimeZoneProvider>
    </I18nProvider>
  )
}
it('keeps the explicit choice through locale changes and drops it when the account changes or unmounts', () => {
  const view = render(<View zone="America/New_York" />)
  expect(screen.getByRole('status').textContent).toBe(
    'Horário do evento: 01/11/2026, 06:30 UTC\nNo seu fuso salvo: 01/11/2026, 01:30 — Horário Nova York · UTC−05:00',
  )
  fireEvent.click(screen.getByText('English'))
  expect(screen.getByRole('status').textContent).toBe(
    'Event time: 11/1/26, 6:30 AM UTC\nIn your saved time zone: 11/1/26, 1:30 AM — ET · UTC−05:00',
  )
  view.rerender(<View zone="America/Sao_Paulo" />)
  expect(screen.getByRole('status').textContent).toBe(
    'Event time: 11/1/26, 6:30 AM UTC\nIn your saved time zone: 11/1/26, 3:30 AM — São Paulo Time · UTC−03:00',
  )
  view.rerender(
    <I18nProvider initialLocale="en-US">
      <Probe />
    </I18nProvider>,
  )
  expect(screen.getByRole('status').textContent).toBe('Event time: 11/1/26, 6:30 AM UTC')
})
it('uses an explicit UTC reference when a saved choice is unsupported by the browser', () => {
  render(<View zone="Mars/Olympus" />)
  expect(screen.getByRole('status').textContent).toBe(
    'Horário do evento: 01/11/2026, 06:30 UTC\nReferência: 01/11/2026, 06:30 UTC; este dispositivo não reconhece o fuso Mars/Olympus',
  )
})
