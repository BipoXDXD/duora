import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '@/test/render'
import { server } from '@/test/msw'
import { syntheticLegalCatalog } from '@/test/legal-documents'

describe('public versioned documents', () => {
  it('shows the requested document language independently of the interface', async () => {
    await renderApp('/terms/test-terms-v2/en-US')
    const text = await screen.findByText('Synthetic terms en-US text.')
    expect(text.closest('[lang]')).toHaveAttribute('lang', 'en-US')
    expect(screen.getByText('2'.repeat(64))).toBeInTheDocument()
  })
  it('does not silently replace an unavailable historical version', async () => {
    await renderApp('/privacy/unknown/pt-BR')
    expect(await screen.findByRole('alert')).toHaveTextContent('Esta versão e idioma')
    expect(screen.queryByText('Synthetic privacy pt-BR text.')).not.toBeInTheDocument()
  })
  it('makes drafts readable without inventing a controller or opening registration', async () => {
    server.use(
      http.get('/api/v1/legal-documents', () =>
        HttpResponse.json({
          ...syntheticLegalCatalog,
          registrationAvailable: false,
          documents: syntheticLegalCatalog.documents.map((document) => ({
            ...document,
            published: false,
            controllerName: null,
          })),
        }),
      ),
    )
    await renderApp('/terms/test-terms-v2/pt-BR')
    expect(await screen.findByText('Synthetic terms pt-BR text.')).toBeInTheDocument()
    expect(screen.getByText('Identidade do controlador ainda não confirmada.')).toBeInTheDocument()
    expect(screen.getByText(/Rascunho público/)).toBeInTheDocument()
  })
})
