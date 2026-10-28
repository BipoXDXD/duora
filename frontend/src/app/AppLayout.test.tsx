import { screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import type { Locale } from '@/shared/i18n/locale'
import { renderWithI18n } from '@/test/render'
import { AppLayout } from './AppLayout'

function renderShell(locale: Locale) {
  return renderWithI18n(
    <MemoryRouter>
      <AppLayout />
    </MemoryRouter>,
    locale,
  )
}

// Owner decision of 2026-10-28 (issue #9): the product does not publish per-country emergency contacts.
describe('public layout without per-country emergency contacts', () => {
  it.each(['pt-BR', 'en-US'] as const)(
    'shows no telephone links, safety anchor or emergency region in %s',
    (locale) => {
      renderShell(locale)

      const links = screen.queryAllByRole('link')
      expect(links.filter((link) => link.getAttribute('href')?.startsWith('tel:'))).toEqual([])
      expect(links.filter((link) => link.getAttribute('href') === '#safety')).toEqual([])
      expect(document.getElementById('safety')).toBeNull()
      expect(within(screen.getByRole('contentinfo')).queryByRole('heading')).toBeNull()
    },
  )
})
