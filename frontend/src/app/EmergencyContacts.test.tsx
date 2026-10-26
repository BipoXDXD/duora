import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { resolveInitialLocale, type Locale } from '@/shared/i18n/locale'
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

describe('official emergency contacts in the public safety area', () => {
  it.each([
    ['pt-BR', 'Segurança e contatos de emergência', ['Brasil', 'Estados Unidos']],
    ['en-US', 'Safety and emergency contacts', ['Brazil', 'United States']],
  ] as const)('shows both labelled countries immediately in %s', (locale, title, [brazil, unitedStates]) => {
    renderShell(locale)

    const safety = screen.getByRole('region', { name: title })
    expect(safety).toBeVisible()
    expect(within(safety).getByRole('heading', { level: 3, name: brazil })).toBeVisible()
    expect(within(safety).getByRole('heading', { level: 3, name: unitedStates })).toBeVisible()
    const phoneLinks = within(safety)
      .getAllByRole('link')
      .filter((link) => link.getAttribute('href')?.startsWith('tel:'))
    expect(phoneLinks.map((link) => link.getAttribute('href'))).toEqual(['tel:190', 'tel:192', 'tel:180', 'tel:911'])
    for (const link of phoneLinks) expect(link).toBeVisible()
  })

  it('keeps country-specific numbers when the interface language changes', async () => {
    const user = userEvent.setup()
    renderShell('pt-BR')

    const brazilianPolice = screen.getByRole('link', { name: 'Brasil: 190 — Polícia Militar, emergência' })
    const americanEmergency = screen.getByRole('link', {
      name: 'Estados Unidos: 911 — emergência policial, de bombeiros ou médica',
    })
    expect(brazilianPolice).toHaveAttribute('href', 'tel:190')
    expect(americanEmergency).toHaveAttribute('href', 'tel:911')

    await user.click(screen.getByRole('button', { name: 'English' }))

    expect(screen.getByRole('link', { name: 'Brazil: 190 — Military Police emergency' })).toHaveAttribute(
      'href',
      'tel:190',
    )
    expect(
      screen.getByRole('link', { name: 'United States: 911 — police, fire or medical emergency' }),
    ).toHaveAttribute('href', 'tel:911')
    expect(document.documentElement.lang).toBe('en-US')
  })

  it('does not select Brazil or open a dialler when country and language are unknown', () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null)
    const previousUrl = window.location.href
    renderShell(resolveInitialLocale({ stored: null, preferred: ['zz-ZZ'] }))

    const safety = screen.getByRole('region', { name: 'Safety and emergency contacts' })
    expect(
      within(safety).getByText(
        'These numbers apply only in the countries shown. If you are elsewhere, contact local emergency services. Duora does not provide emergency assistance.',
      ),
    ).toBeVisible()
    expect(
      within(safety)
        .getAllByRole('link')
        .filter((link) => link.getAttribute('href')?.startsWith('tel:')),
    ).toHaveLength(4)
    expect(within(safety).getByRole('heading', { name: 'Brazil' })).toBeVisible()
    expect(within(safety).getByRole('heading', { name: 'United States' })).toBeVisible()
    expect(open).not.toHaveBeenCalled()
    expect(window.location.href).toBe(previousUrl)
  })

  it.each([
    ['pt-BR', 'Brasil: 180 — orientação e denúncia de violência contra a mulher; não é emergência'],
    ['en-US', 'Brazil: 180 — information and reporting violence against women; not an emergency service'],
  ] as const)('distinguishes 180 from emergency services in %s', (locale, label) => {
    renderShell(locale)

    expect(screen.getByRole('link', { name: label })).toHaveAttribute('href', 'tel:180')
  })

  it('offers a keyboard-reachable safety link and country-labelled phone links without signing in', async () => {
    const user = userEvent.setup()
    renderShell('en-US')

    await user.tab()
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('link', { name: 'Duora' })).toHaveFocus()
    await user.tab()
    const safetyLink = screen.getByRole('link', { name: 'Safety' })
    expect(safetyLink).toHaveFocus()
    expect(safetyLink).toHaveAttribute('href', '#safety')
    const safety = screen.getByRole('region', { name: 'Safety and emergency contacts' })
    expect(safety).toHaveAttribute('id', 'safety')
    await user.tab()
    await user.tab()
    await user.tab()
    expect(screen.getByRole('link', { name: 'Brazil: 190 — Military Police emergency' })).toHaveFocus()
  })
})
